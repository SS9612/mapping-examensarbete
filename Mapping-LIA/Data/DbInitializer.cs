using Mapping_LIA.Entities;
using Mapping_LIA.Services.Normalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Mapping_LIA.Data;

/// <summary>
/// Startup seeding and one-time migration helpers for the mapping taxonomy.
/// </summary>
/// <remarks>
/// These methods run from Program.cs on application startup. They are deliberately
/// idempotent because the app has been used with evolving local databases during
/// development and handoff.
/// </remarks>
public static class DbInitializer
{
    public static void SeedData(ApplicationDbContext context)
    {
        context.Database.Migrate();

        if (context.Areas.Any())
        {
            return;
        }

        var data = GetCompetenceData();

        var areasDict = new Dictionary<Guid, Area>();
        var categoriesDict = new Dictionary<Guid, Category>();

        foreach (var item in data)
        {
            if (!areasDict.ContainsKey(item.AreaId))
            {
                var area = new Area
                {
                    AreaId = item.AreaId,
                    Name = item.Area
                };
                areasDict[item.AreaId] = area;
                context.Areas.Add(area);
            }

            if (!categoriesDict.ContainsKey(item.CategoryId))
            {
                var category = new Category
                {
                    CategoryId = item.CategoryId,
                    Name = item.Category,
                    AreaId = item.AreaId
                };
                categoriesDict[item.CategoryId] = category;
                context.Categories.Add(category);
            }

            var subcategory = new Subcategory
            {
                SubcategoryId = item.SubcategoryId,
                Name = item.Subcategory,
                CategoryId = item.CategoryId
            };
            context.Subcategories.Add(subcategory);
        }

        context.SaveChanges();
    }

    public static void SeedAdminUser(ApplicationDbContext context)
    {
        if (context.Users.Any(u => u.Username == "admin"))
        {
            return;
        }

        var adminUser = new User
        {
            UserId = Guid.NewGuid(),
            Username = "admin",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("SigmaTech123!"),
            CreatedAt = DateTime.UtcNow
        };

        context.Users.Add(adminUser);
        context.SaveChanges();
    }

    /// <summary>
    /// Copies rows from the legacy ImportCompetences table into the current Competences table.
    /// </summary>
    /// <remarks>
    /// Some local or deployed databases do not have the legacy table. The method
    /// skips transfer when it is missing so new environments can still start with
    /// only the seeded taxonomy.
    /// </remarks>
    public static async Task TransferImportCompetencesAsync(
        ApplicationDbContext context,
        ITextNormalizer normalizer,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        // Check if ImportCompetences table exists by attempting to query it (graceful degradation if table missing)
        try
        {
            var testQuery = await context.Database
                .SqlQueryRaw<ImportCompetenceRow>("SELECT TOP 1 Area, Category, Subcategory, Competence FROM dbo.ImportCompetences")
                .FirstOrDefaultAsync(ct);
        }
        catch (Exception ex) when (ex.Message.Contains("Invalid object name") || ex.Message.Contains("does not exist"))
        {
            logger?.LogInformation("ImportCompetences table does not exist. Skipping competence transfer.");
            return;
        }

        // Check if competences have already been transferred
        // Guard on MatchedType = "Seeded" so it works for both historical Approved+Seeded
        // records and the new LegacyImported status.
        if (await context.Competences.AnyAsync(c => c.MatchedType == "Seeded", ct))
        {
            logger?.LogInformation("Competences have already been transferred from ImportCompetences. Skipping.");
            return;
        }

        logger?.LogInformation("Starting transfer of competences from ImportCompetences to Competences...");

        // Query ImportCompetences
        var importData = await context.Database
            .SqlQueryRaw<ImportCompetenceRow>(
                "SELECT Area, Category, Subcategory, Competence FROM dbo.ImportCompetences WHERE Competence IS NOT NULL AND Competence != ''")
            .ToListAsync(ct);

        if (!importData.Any())
        {
            logger?.LogWarning("No competences found in ImportCompetences table.");
            return;
        }

        logger?.LogInformation("Found {Count} competences to transfer from ImportCompetences.", importData.Count);

        // Build lookup dictionaries for Area, Category, Subcategory by name
        var areas = await context.Areas.AsNoTracking().ToListAsync(ct);
        var categories = await context.Categories.AsNoTracking().Include(c => c.Area).ToListAsync(ct);
        var subcategories = await context.Subcategories.AsNoTracking().Include(s => s.Category).ThenInclude(c => c.Area).ToListAsync(ct);

        var areaLookup = areas.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        var categoryLookup = categories.ToDictionary(c => (c.Area?.Name ?? "", c.Name), new CategoryKeyComparer());
        var subcategoryLookup = subcategories.ToDictionary(s => (s.Category?.Area?.Name ?? "", s.Category?.Name ?? "", s.Name), new SubcategoryKeyComparer());

        var transferred = 0;
        var skipped = 0;
        var errors = 0;

        // Track normalized values we've already added in this batch to avoid duplicates within the same import
        var normalizedInBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Load existing normalized values from database once to avoid per-row queries
        var existingNormalizedList = await context.Competences
            .AsNoTracking()
            .Select(c => c.Normalized)
            .ToListAsync(ct);
        var existingNormalized = new HashSet<string>(existingNormalizedList, StringComparer.OrdinalIgnoreCase);

        foreach (var row in importData)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(row.Competence))
                    continue;

                if (!areaLookup.TryGetValue(row.Area ?? "", out var area))
                {
                    logger?.LogWarning("Area '{Area}' not found for competence '{Competence}'. Skipping.", row.Area, row.Competence);
                    skipped++;
                    continue;
                }

                Category? category = null;
                if (!string.IsNullOrWhiteSpace(row.Category))
                {
                    if (!categoryLookup.TryGetValue((row.Area ?? "", row.Category), out category))
                    {
                        logger?.LogWarning("Category '{Category}' in Area '{Area}' not found for competence '{Competence}'. Skipping.", row.Category, row.Area, row.Competence);
                        skipped++;
                        continue;
                    }
                }

                Subcategory? subcategory = null;
                if (!string.IsNullOrWhiteSpace(row.Subcategory) && category != null)
                {
                    if (!subcategoryLookup.TryGetValue((row.Area ?? "", row.Category ?? "", row.Subcategory), out subcategory))
                    {
                        logger?.LogWarning("Subcategory '{Subcategory}' in Category '{Category}' not found for competence '{Competence}'. Skipping.", row.Subcategory, row.Category, row.Competence);
                        skipped++;
                        continue;
                    }
                }

                var normalized = normalizer.Normalize(row.Competence);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    logger?.LogWarning("Could not normalize competence '{Competence}'. Skipping.", row.Competence);
                    skipped++;
                    continue;
                }

                // Check for duplicate
                if (existingNormalized.Contains(normalized) || normalizedInBatch.Contains(normalized))
                {
                    logger?.LogDebug("Competence '{Competence}' (normalized: '{Normalized}') already exists. Skipping.", row.Competence, normalized);
                    skipped++;
                    continue;
                }

                // Track this normalized value to avoid duplicates in the same batch
                normalizedInBatch.Add(normalized);

                // Create competence entity
                var competence = new Competence
                {
                    CompetenceId = Guid.NewGuid(),
                    Name = row.Competence.Trim(),
                    Normalized = normalized,
                    AreaId = area.AreaId,
                    CategoryId = category?.CategoryId,
                    SubcategoryId = subcategory?.SubcategoryId,
                    // Imported legacy competences live in their own status and are not treated
                    // as newly approved LLM-reviewed items.
                    Status = CompetenceStatus.LegacyImported,
                    Confidence = 0.5,
                    MatchedType = "Seeded",
                    CreatedAt = DateTime.UtcNow,
                    ReviewedAt = null,
                    ReviewNotes = null
                };

                context.Competences.Add(competence);
                transferred++;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error transferring competence '{Competence}'. Skipping.", row.Competence);
                errors++;
            }
        }

        if (transferred > 0)
        {
            await context.SaveChangesAsync(ct);
            logger?.LogInformation("Successfully transferred {Transferred} competences from ImportCompetences. Skipped: {Skipped}, Errors: {Errors}", transferred, skipped, errors);
        }
        else
        {
            logger?.LogInformation("No competences were transferred. Skipped: {Skipped}, Errors: {Errors}", skipped, errors);
        }
    }

    /// <summary>
    /// Migrate historical legacy competences that were previously stored as
    /// Status = Approved and MatchedType = "Seeded" into the dedicated
    /// LegacyImported status so they are not treated as newly approved items.
    /// </summary>
    public static async Task MigrateLegacyCompetencesAsync(
        ApplicationDbContext context,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var legacyApproved = await context.Competences
            .Where(c => c.Status == CompetenceStatus.Approved && c.MatchedType == "Seeded")
            .ToListAsync(ct);

        if (!legacyApproved.Any())
        {
            logger?.LogInformation("No historical Approved+Seeded competences found to migrate.");
            return;
        }

        foreach (var competence in legacyApproved)
        {
            competence.Status = CompetenceStatus.LegacyImported;
            // These were not explicitly reviewed in the new system
            competence.ReviewedAt = null;
            if (competence.Confidence <= 0 || competence.Confidence > 1.0)
            {
                competence.Confidence = 0.5;
            }
        }

        await context.SaveChangesAsync(ct);
        logger?.LogInformation("Migrated {Count} historical Approved+Seeded competences to LegacyImported status.", legacyApproved.Count);
    }

    private class ImportCompetenceRow
    {
        public string? Area { get; set; }
        public string? Category { get; set; }
        public string? Subcategory { get; set; }
        public string? Competence { get; set; }
    }

    private class CategoryKeyComparer : IEqualityComparer<(string AreaName, string CategoryName)>
    {
        public bool Equals((string AreaName, string CategoryName) x, (string AreaName, string CategoryName) y)
        {
            return string.Equals(x.AreaName, y.AreaName, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(x.CategoryName, y.CategoryName, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((string AreaName, string CategoryName) obj)
        {
            return HashCode.Combine(
                obj.AreaName?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0,
                obj.CategoryName?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0);
        }
    }

    private class SubcategoryKeyComparer : IEqualityComparer<(string AreaName, string CategoryName, string SubcategoryName)>
    {
        public bool Equals((string AreaName, string CategoryName, string SubcategoryName) x, (string AreaName, string CategoryName, string SubcategoryName) y)
        {
            return string.Equals(x.AreaName, y.AreaName, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(x.CategoryName, y.CategoryName, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(x.SubcategoryName, y.SubcategoryName, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((string AreaName, string CategoryName, string SubcategoryName) obj)
        {
            return HashCode.Combine(
                obj.AreaName?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0,
                obj.CategoryName?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0,
                obj.SubcategoryName?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0);
        }
    }

    private static List<CompetenceData> GetCompetenceData()
    {
        var data = new List<CompetenceData>
        {
            // Civil Engineering - Environmental Consulting
            new(Guid.Parse("7C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Civil Engineering", Guid.Parse("856F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Environmental Consulting", Guid.Parse("C86F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Energy"),
            new(Guid.Parse("7C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Civil Engineering", Guid.Parse("856F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Environmental Consulting", Guid.Parse("CE6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Enviroment in Buildings"),
            new(Guid.Parse("7C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Civil Engineering", Guid.Parse("856F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Environmental Consulting", Guid.Parse("CD6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Environmental Consulting"),
            new(Guid.Parse("7C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Civil Engineering", Guid.Parse("856F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Environmental Consulting", Guid.Parse("D06F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Environmental Coordination"),
            new(Guid.Parse("7C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Civil Engineering", Guid.Parse("856F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Environmental Consulting", Guid.Parse("CF6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Environmental Impact Description"),
            new(Guid.Parse("7C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Civil Engineering", Guid.Parse("856F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Environmental Consulting", Guid.Parse("CB6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IT Tools"),
            new(Guid.Parse("7C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Civil Engineering", Guid.Parse("856F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Environmental Consulting", Guid.Parse("CC6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Management Systems"),
            new(Guid.Parse("7C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Civil Engineering", Guid.Parse("856F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Environmental Consulting", Guid.Parse("D16F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Nature Inventories"),
            new(Guid.Parse("7C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Civil Engineering", Guid.Parse("856F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Environmental Consulting", Guid.Parse("C96F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Polluted Areas"),
            new(Guid.Parse("7C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Civil Engineering", Guid.Parse("856F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Environmental Consulting", Guid.Parse("D36F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Risk & Safety"),
            new(Guid.Parse("7C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Civil Engineering", Guid.Parse("856F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Environmental Consulting", Guid.Parse("D46F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Strategical Enviromental Services"),
            new(Guid.Parse("7C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Civil Engineering", Guid.Parse("856F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Environmental Consulting", Guid.Parse("CA6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Sustainability & Climate"),
            new(Guid.Parse("7C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Civil Engineering", Guid.Parse("856F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Environmental Consulting", Guid.Parse("D56F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Water Engineering"),
            new(Guid.Parse("7C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Civil Engineering", Guid.Parse("856F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Environmental Consulting", Guid.Parse("D26F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Verification procedures according to MB"),
            
            // Civil Engineering - Geotechnical Engineering
            new(Guid.Parse("7C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Civil Engineering", Guid.Parse("826F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Geotechnical Engineering", Guid.Parse("C26F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Geotechnical Engineering"),
            new(Guid.Parse("7C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Civil Engineering", Guid.Parse("826F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Geotechnical Engineering", Guid.Parse("C36F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Hydrogeology"),
            new(Guid.Parse("7C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Civil Engineering", Guid.Parse("826F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Geotechnical Engineering", Guid.Parse("C46F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IT Tools"),
            
            // Civil Engineering - Project Management
            new(Guid.Parse("7C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Civil Engineering", Guid.Parse("976F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Project Management", Guid.Parse("31705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Project Management - Civil Engineering"),
            
            // Civil Engineering - Rail Design and Engineering
            new(Guid.Parse("7C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Civil Engineering", Guid.Parse("866F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Rail Design and Engineering", Guid.Parse("D66F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Railway Civil Engineering"),
            
            // Civil Engineering - Road and Landscape Architecture and Design
            new(Guid.Parse("7C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Civil Engineering", Guid.Parse("846F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Road and Landscape Architecture and Design", Guid.Parse("C76F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IT Tools"),
            new(Guid.Parse("7C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Civil Engineering", Guid.Parse("846F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Road and Landscape Architecture and Design", Guid.Parse("C66F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Road and Landscape Architecture and Design"),
            
            // Civil Engineering - Road Design and Traffic Planning
            new(Guid.Parse("7C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Civil Engineering", Guid.Parse("886F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Road Design and Traffic Planning", Guid.Parse("D96F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Construction and Project Management"),
            new(Guid.Parse("7C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Civil Engineering", Guid.Parse("886F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Road Design and Traffic Planning", Guid.Parse("DA6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IT Tools"),
            new(Guid.Parse("7C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Civil Engineering", Guid.Parse("886F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Road Design and Traffic Planning", Guid.Parse("DB6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Land and Road"),
            
            // Civil Engineering - Structural Engineering
            new(Guid.Parse("7C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Civil Engineering", Guid.Parse("836F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Structural Engineering", Guid.Parse("C56F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Structural Engineering"),
            
            // Civil Engineering - Water and Wastewater Engineering
            new(Guid.Parse("7C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Civil Engineering", Guid.Parse("876F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Water and Wastewater Engineering", Guid.Parse("D76F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IT Tools"),
            new(Guid.Parse("7C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Civil Engineering", Guid.Parse("876F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Water and Wastewater Engineering", Guid.Parse("D86F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Water and Wastewater Engineering"),
            
            // Engineering & Design - Calculation & Simulation
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("896F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Calculation & Simulation", Guid.Parse("DC6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Calculation Norms"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("896F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Calculation & Simulation", Guid.Parse("DD6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Electromagnetism"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("896F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Calculation & Simulation", Guid.Parse("E46F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Fluid Dynamics"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("896F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Calculation & Simulation", Guid.Parse("DF6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Mathematics"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("896F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Calculation & Simulation", Guid.Parse("E06F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Multiphysics"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("896F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Calculation & Simulation", Guid.Parse("E16F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Optimization"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("896F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Calculation & Simulation", Guid.Parse("E26F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Simulation"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("896F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Calculation & Simulation", Guid.Parse("DE6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Strength of Materials and Mechanics"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("896F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Calculation & Simulation", Guid.Parse("E36F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Structure Acoustics"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("896F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Calculation & Simulation", Guid.Parse("E56F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Thermodynamics"),
            
            // Engineering & Design - Industrial Design & Ergonomics
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8A6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Industrial Design & Ergonomics", Guid.Parse("E66F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Ergonomics"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8A6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Industrial Design & Ergonomics", Guid.Parse("E86F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Idea and Conceptual Phase"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8A6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Industrial Design & Ergonomics", Guid.Parse("E96F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Industrial Design"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8A6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Industrial Design & Ergonomics", Guid.Parse("E76F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Shape Determination"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8A6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Industrial Design & Ergonomics", Guid.Parse("EA6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Tools"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8A6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Industrial Design & Ergonomics", Guid.Parse("EB6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Visualization"),
            
            // Engineering & Design - Manufacturing engineering incl. Logistics
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Manufacturing engineering incl. Logistics", Guid.Parse("FA6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "CAM/NC Preparation"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Manufacturing engineering incl. Logistics", Guid.Parse("12705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Coating Technology"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Manufacturing engineering incl. Logistics", Guid.Parse("01705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Commissioning Robotics"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Manufacturing engineering incl. Logistics", Guid.Parse("FB6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Cost Engineering"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Manufacturing engineering incl. Logistics", Guid.Parse("FC6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Distribution Logistics"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Manufacturing engineering incl. Logistics", Guid.Parse("FD6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Documentation Robotics"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Manufacturing engineering incl. Logistics", Guid.Parse("08705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Environmental Engineering - Production"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Manufacturing engineering incl. Logistics", Guid.Parse("FE6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Fixture Robotics"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Manufacturing engineering incl. Logistics", Guid.Parse("FF6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Flow Simulation Production"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Manufacturing engineering incl. Logistics", Guid.Parse("05705820-4EBC-EF11-8597-DC1BA18BC7B5"), "General Logistics"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Manufacturing engineering incl. Logistics", Guid.Parse("02705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Installation Management Robotics "),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Manufacturing engineering incl. Logistics", Guid.Parse("00705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Joining Technology - Production"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Manufacturing engineering incl. Logistics", Guid.Parse("04705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Lean-production"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Manufacturing engineering incl. Logistics", Guid.Parse("06705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Material Handling/Logistics"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Manufacturing engineering incl. Logistics", Guid.Parse("07705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Materials Engineering"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Manufacturing engineering incl. Logistics", Guid.Parse("09705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Process Preparation - Production"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Manufacturing engineering incl. Logistics", Guid.Parse("0A705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Process Preparation - Robotics"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Manufacturing engineering incl. Logistics", Guid.Parse("F96F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Processing - Production"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Manufacturing engineering incl. Logistics", Guid.Parse("0B705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Production Logistics"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Manufacturing engineering incl. Logistics", Guid.Parse("0C705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Production Technology"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Manufacturing engineering incl. Logistics", Guid.Parse("03705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Quality Technology QA/SQA"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Manufacturing engineering incl. Logistics", Guid.Parse("0D705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Robot Programming"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Manufacturing engineering incl. Logistics", Guid.Parse("0E705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Simulation Robotics"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Manufacturing engineering incl. Logistics", Guid.Parse("10705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Technique Preparation Robot"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Manufacturing engineering incl. Logistics", Guid.Parse("11705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Transport Logistics"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Manufacturing engineering incl. Logistics", Guid.Parse("0F705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Welding Guard Programming Robotics"),
            
            // Engineering & Design - Mechanical Engineering
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Mechanical Engineering", Guid.Parse("ED6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "CAD/PDM Tools"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Mechanical Engineering", Guid.Parse("F26F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Construction Elements"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Mechanical Engineering", Guid.Parse("EE6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Detail Engineering"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Mechanical Engineering", Guid.Parse("F16F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Hydraulics & Pneumatics"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Mechanical Engineering", Guid.Parse("F06F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Joining Technology - Construction"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Mechanical Engineering", Guid.Parse("EC6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Manufacturing - Construction"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Mechanical Engineering", Guid.Parse("F36F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Materials Science - Construction"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Mechanical Engineering", Guid.Parse("EF6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Packaging - Construction Skills"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Mechanical Engineering", Guid.Parse("F46F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Product Care"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Mechanical Engineering", Guid.Parse("F56F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Prototype Design/Development"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Mechanical Engineering", Guid.Parse("F86F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Surface Treatment & Corrosion Protection"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Mechanical Engineering", Guid.Parse("F66F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Technical Testing"),
            new(Guid.Parse("7D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Engineering & Design", Guid.Parse("8B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Mechanical Engineering", Guid.Parse("F76F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Tool Design"),
            
            // IS/IT and Communication - Business Analysis
            new(Guid.Parse("7B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IS/IT and Communication", Guid.Parse("A16F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Business Analysis", Guid.Parse("57705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Business Analysis"),
            
            // IS/IT and Communication - Business Intelligence
            new(Guid.Parse("7B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IS/IT and Communication", Guid.Parse("9C6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Business Intelligence", Guid.Parse("B76F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Business Intelligence"),
            
            // IS/IT and Communication - Business Systems
            new(Guid.Parse("7B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IS/IT and Communication", Guid.Parse("9D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Business Systems", Guid.Parse("B86F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Business Systems"),
            
            // IS/IT and Communication - Digital Communication
            new(Guid.Parse("7B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IS/IT and Communication", Guid.Parse("A26F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Digital Communication", Guid.Parse("58705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Digital Communication"),
            
            // IS/IT and Communication - Operation, support and infrastructure
            new(Guid.Parse("7B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IS/IT and Communication", Guid.Parse("9F6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Operation, support and infrastructure", Guid.Parse("BA6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Infrastructure"),
            new(Guid.Parse("7B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IS/IT and Communication", Guid.Parse("9F6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Operation, support and infrastructure", Guid.Parse("BC6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IT Security"),
            new(Guid.Parse("7B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IS/IT and Communication", Guid.Parse("9F6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Operation, support and infrastructure", Guid.Parse("BD6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Operating Systems"),
            
            // IS/IT and Communication - Project Management & Management
            new(Guid.Parse("7B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IS/IT and Communication", Guid.Parse("916F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Project Management & Management", Guid.Parse("36705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Communication"),
            new(Guid.Parse("7B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IS/IT and Communication", Guid.Parse("916F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Project Management & Management", Guid.Parse("2F705820-4EBC-EF11-8597-DC1BA18BC7B5"), "IT Management"),
            new(Guid.Parse("7B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IS/IT and Communication", Guid.Parse("916F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Project Management & Management", Guid.Parse("37705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Planning"),
            new(Guid.Parse("7B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IS/IT and Communication", Guid.Parse("916F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Project Management & Management", Guid.Parse("30705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Project Management"),
            new(Guid.Parse("7B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IS/IT and Communication", Guid.Parse("916F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Project Management & Management", Guid.Parse("38705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Project Management and Project Models"),
            new(Guid.Parse("7B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IS/IT and Communication", Guid.Parse("916F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Project Management & Management", Guid.Parse("39705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Risk Management"),
            new(Guid.Parse("7B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IS/IT and Communication", Guid.Parse("916F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Project Management & Management", Guid.Parse("3A705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Team Development"),
            
            // IS/IT and Communication - Requirement Management & Business Development
            new(Guid.Parse("7B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IS/IT and Communication", Guid.Parse("A36F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Requirement Management & Business Development", Guid.Parse("59705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Requirement Management & Business Development"),
            
            // IS/IT and Communication - System development
            new(Guid.Parse("7B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IS/IT and Communication", Guid.Parse("9E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "System development", Guid.Parse("B96F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Databases"),
            new(Guid.Parse("7B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IS/IT and Communication", Guid.Parse("9E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "System development", Guid.Parse("BE6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Platforms"),
            new(Guid.Parse("7B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IS/IT and Communication", Guid.Parse("9E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "System development", Guid.Parse("BF6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Programming Languages"),
            new(Guid.Parse("7B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IS/IT and Communication", Guid.Parse("9E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "System development", Guid.Parse("C16F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Tools"),
            
            // IS/IT and Communication - Test & Quality
            new(Guid.Parse("7B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IS/IT and Communication", Guid.Parse("A06F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Test & Quality", Guid.Parse("56705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Quality"),
            new(Guid.Parse("7B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IS/IT and Communication", Guid.Parse("A06F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Test & Quality", Guid.Parse("55705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Test & Test Management"),
            new(Guid.Parse("7B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IS/IT and Communication", Guid.Parse("A06F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Test & Quality", Guid.Parse("6E705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Tools"),
            
            // Other
            new(Guid.Parse("806F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("A46F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("32705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Business Systems"),
            new(Guid.Parse("806F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("A46F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("4C705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Channelization"),
            new(Guid.Parse("806F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("A46F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("34705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Document Management Systems"),
            new(Guid.Parse("806F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("A46F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("35705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Earned Value"),
            new(Guid.Parse("806F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("A46F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("4B705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Electric Power"),
            new(Guid.Parse("806F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("A46F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("BB6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "IT Area"),
            new(Guid.Parse("806F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("A46F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("50705820-4EBC-EF11-8597-DC1BA18BC7B5"), "IT Tools"),
            new(Guid.Parse("806F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("A46F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("33705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Manager Competence"),
            new(Guid.Parse("806F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("A46F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("3D705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other Methodologies and Organizational Development"),
            new(Guid.Parse("806F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("A46F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("3C705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Procurement/Purchasing"),
            new(Guid.Parse("806F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("A46F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("4D705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Signaling"),
            new(Guid.Parse("806F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("A46F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("C06F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Standards, Methods & Models"),
            new(Guid.Parse("806F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("A46F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("4E705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Tele"),
            new(Guid.Parse("806F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("A46F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("4F705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Traffic Management"),
            new(Guid.Parse("806F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("A46F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other", Guid.Parse("3B705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Transition Management"),
            
            // Product Information - Client Tools & Skills
            new(Guid.Parse("816F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Product Information", Guid.Parse("A76F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Client Tools & Skills", Guid.Parse("5F705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Ericsson"),
            new(Guid.Parse("816F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Product Information", Guid.Parse("A76F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Client Tools & Skills", Guid.Parse("62705820-4EBC-EF11-8597-DC1BA18BC7B5"), "GE"),
            new(Guid.Parse("816F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Product Information", Guid.Parse("A76F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Client Tools & Skills", Guid.Parse("65705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Kockums"),
            new(Guid.Parse("816F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Product Information", Guid.Parse("A76F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Client Tools & Skills", Guid.Parse("63705820-4EBC-EF11-8597-DC1BA18BC7B5"), "SAAB"),
            new(Guid.Parse("816F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Product Information", Guid.Parse("A76F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Client Tools & Skills", Guid.Parse("61705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Sigma"),
            new(Guid.Parse("816F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Product Information", Guid.Parse("A76F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Client Tools & Skills", Guid.Parse("64705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Sony"),
            new(Guid.Parse("816F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Product Information", Guid.Parse("A76F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Client Tools & Skills", Guid.Parse("60705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Tetra Pak"),
            new(Guid.Parse("816F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Product Information", Guid.Parse("A76F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Client Tools & Skills", Guid.Parse("5E705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Volvo"),
            
            // Product Information - Field of Experience
            new(Guid.Parse("816F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Product Information", Guid.Parse("A96F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Field of Experience", Guid.Parse("6A705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Design"),
            new(Guid.Parse("816F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Product Information", Guid.Parse("A96F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Field of Experience", Guid.Parse("69705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Field of Experience"),
            new(Guid.Parse("816F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Product Information", Guid.Parse("A96F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Field of Experience", Guid.Parse("6B705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other"),
            
            // Product Information - Generic
            new(Guid.Parse("816F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Product Information", Guid.Parse("A86F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Generic", Guid.Parse("68705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Languages"),
            new(Guid.Parse("816F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Product Information", Guid.Parse("A86F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Generic", Guid.Parse("67705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other"),
            new(Guid.Parse("816F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Product Information", Guid.Parse("A86F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Generic", Guid.Parse("66705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Standards/Methods"),
            
            // Product Information - Product
            new(Guid.Parse("816F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Product Information", Guid.Parse("AB6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Product", Guid.Parse("6D705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Product"),
            
            // Product Information - Role Experience
            new(Guid.Parse("816F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Product Information", Guid.Parse("AA6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Role Experience", Guid.Parse("6C705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Role Experience"),
            
            // Product Information - Software
            new(Guid.Parse("816F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Product Information", Guid.Parse("A66F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software", Guid.Parse("5C705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Adobe"),
            new(Guid.Parse("816F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Product Information", Guid.Parse("A66F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software", Guid.Parse("5B705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Microsoft"),
            new(Guid.Parse("816F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Product Information", Guid.Parse("A66F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software", Guid.Parse("5D705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other"),
            
            // Software and Electrical Engineering - Electricity & Automation
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("8D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Electricity & Automation", Guid.Parse("13705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Drives"),
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("8D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Electricity & Automation", Guid.Parse("14705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Electrical Design"),
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("8D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Electricity & Automation", Guid.Parse("16705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Fieldbus Technology/Communication"),
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("8D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Electricity & Automation", Guid.Parse("15705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Frequency Inverters"),
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("8D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Electricity & Automation", Guid.Parse("19705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Other Electrical & Automation"),
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("8D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Electricity & Automation", Guid.Parse("17705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Programming & Programming Languages (PLC/HMI/SCADA/DCS)"),
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("8D6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Electricity & Automation", Guid.Parse("18705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Vision Systems"),
            
            // Software and Electrical Engineering - Energy & Power Engineering
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("8E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Energy & Power Engineering", Guid.Parse("1B705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Energy Technologies"),
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("8E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Energy & Power Engineering", Guid.Parse("1C705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Gas Technologies"),
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("8E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Energy & Power Engineering", Guid.Parse("20705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Hydroelectric Expertise"),
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("8E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Energy & Power Engineering", Guid.Parse("1D705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Nuclear Power Expertise"),
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("8E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Energy & Power Engineering", Guid.Parse("1A705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Power Engineering"),
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("8E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Energy & Power Engineering", Guid.Parse("1E705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Simulation Tools Electrical Power"),
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("8E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Energy & Power Engineering", Guid.Parse("1F705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Solar Energy Expertise"),
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("8E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Energy & Power Engineering", Guid.Parse("21705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Thermal Exchange"),
            
            // Software and Electrical Engineering - Function development
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("9A6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Function development", Guid.Parse("53705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Function development"),
            
            // Software and Electrical Engineering - Hardware Electronics
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("8F6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Hardware Electronics", Guid.Parse("28705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Communication Technologies"),
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("8F6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Hardware Electronics", Guid.Parse("2B705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Development Environments"),
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("8F6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Hardware Electronics", Guid.Parse("24705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Electrical Environment"),
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("8F6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Hardware Electronics", Guid.Parse("22705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Electronic Design"),
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("8F6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Hardware Electronics", Guid.Parse("23705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Electronics Simulation"),
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("8F6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Hardware Electronics", Guid.Parse("26705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Hardware Environments"),
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("8F6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Hardware Electronics", Guid.Parse("27705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Hardware Platforms"),
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("8F6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Hardware Electronics", Guid.Parse("25705820-4EBC-EF11-8597-DC1BA18BC7B5"), "HDL Languages"),
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("8F6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Hardware Electronics", Guid.Parse("29705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Logic Environments"),
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("8F6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Hardware Electronics", Guid.Parse("2A705820-4EBC-EF11-8597-DC1BA18BC7B5"), "PCB"),
            
            // Software and Electrical Engineering - Project Management
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("A56F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Project Management", Guid.Parse("5A705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Project Management"),
            
            // Software and Electrical Engineering - RF And Microwave
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("996F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "RF And Microwave", Guid.Parse("52705820-4EBC-EF11-8597-DC1BA18BC7B5"), "RF And Microwave"),
            
            // Software and Electrical Engineering - Software development embedded systems
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("986F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software development embedded systems", Guid.Parse("51705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software development embedded systems"),
            
            // Software and Electrical Engineering - System design
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("9B6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "System design", Guid.Parse("54705820-4EBC-EF11-8597-DC1BA18BC7B5"), "System design"),
            
            // Software and Electrical Engineering - Testing and Verification
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("906F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Testing and Verification", Guid.Parse("2D705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Electronics Testing"),
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("906F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Testing and Verification", Guid.Parse("2C705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Test Methods / Development Test Equipment"),
            new(Guid.Parse("7E6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Software and Electrical Engineering", Guid.Parse("906F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Testing and Verification", Guid.Parse("2E705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Test Simulation / Validation"),
            
            // Special focus areas - Life Science, QA & Validation
            new(Guid.Parse("7F6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Special focus areas", Guid.Parse("926F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Life Science, QA & Validation", Guid.Parse("41705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Clinical Trials"),
            new(Guid.Parse("7F6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Special focus areas", Guid.Parse("926F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Life Science, QA & Validation", Guid.Parse("3E705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Compliance"),
            new(Guid.Parse("7F6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Special focus areas", Guid.Parse("926F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Life Science, QA & Validation", Guid.Parse("4A705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Food"),
            new(Guid.Parse("7F6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Special focus areas", Guid.Parse("926F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Life Science, QA & Validation", Guid.Parse("40705820-4EBC-EF11-8597-DC1BA18BC7B5"), "In Vitro Medical Devices"),
            new(Guid.Parse("7F6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Special focus areas", Guid.Parse("926F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Life Science, QA & Validation", Guid.Parse("43705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Medical Technology"),
            new(Guid.Parse("7F6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Special focus areas", Guid.Parse("926F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Life Science, QA & Validation", Guid.Parse("3F705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Pharmacy"),
            new(Guid.Parse("7F6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Special focus areas", Guid.Parse("926F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Life Science, QA & Validation", Guid.Parse("44705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Production"),
            new(Guid.Parse("7F6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Special focus areas", Guid.Parse("926F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Life Science, QA & Validation", Guid.Parse("45705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Quality Assurance"),
            new(Guid.Parse("7F6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Special focus areas", Guid.Parse("926F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Life Science, QA & Validation", Guid.Parse("46705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Regulatory Affairs"),
            new(Guid.Parse("7F6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Special focus areas", Guid.Parse("926F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Life Science, QA & Validation", Guid.Parse("42705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Requirements Management"),
            new(Guid.Parse("7F6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Special focus areas", Guid.Parse("926F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Life Science, QA & Validation", Guid.Parse("47705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Risk Management"),
            new(Guid.Parse("7F6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Special focus areas", Guid.Parse("926F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Life Science, QA & Validation", Guid.Parse("49705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Tools - Quality Assurance"),
            new(Guid.Parse("7F6F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Special focus areas", Guid.Parse("926F5820-4EBC-EF11-8597-DC1BA18BC7B5"), "Life Science, QA & Validation", Guid.Parse("48705820-4EBC-EF11-8597-DC1BA18BC7B5"), "Validation")
        };

        return data;
    }

    private record CompetenceData(Guid AreaId, string Area, Guid CategoryId, string Category, Guid SubcategoryId, string Subcategory);
}

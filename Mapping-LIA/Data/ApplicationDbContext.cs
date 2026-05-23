using Microsoft.EntityFrameworkCore;
using Mapping_LIA.Entities;

namespace Mapping_LIA.Data;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Area> Areas { get; set; }
    public DbSet<Category> Categories { get; set; }
    public DbSet<Subcategory> Subcategories { get; set; }
    public DbSet<AreaMapping> AreaMappings { get; set; }
    public DbSet<Competence> Competences { get; set; }
    public DbSet<User> Users { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Category>()
            .HasOne(c => c.Area)
            .WithMany(a => a.Categories)
            .HasForeignKey(c => c.AreaId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Subcategory>()
            .HasOne(s => s.Category)
            .WithMany(c => c.Subcategories)
            .HasForeignKey(s => s.CategoryId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Competence>()
            .HasOne(c => c.Area)
            .WithMany()
            .HasForeignKey(c => c.AreaId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<Competence>()
            .HasOne(c => c.Category)
            .WithMany()
            .HasForeignKey(c => c.CategoryId)
            .OnDelete(DeleteBehavior.NoAction);

        modelBuilder.Entity<Competence>()
            .HasOne(c => c.Subcategory)
            .WithMany()
            .HasForeignKey(c => c.SubcategoryId)
            .OnDelete(DeleteBehavior.NoAction);

        // Unique index on normalized name prevents duplicate competences (case-insensitive, normalized)
        modelBuilder.Entity<Competence>()
            .HasIndex(c => c.Normalized)
            .IsUnique();

        // Unique index on username
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Username)
            .IsUnique();

    }
}

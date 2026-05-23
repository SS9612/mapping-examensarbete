using Mapping_LIA.Data;
using Mapping_LIA.Services.AreaMapper;
using Mapping_LIA.Services.Normalization;
using Mapping_LIA.Services.Review;
using Mapping_LIA.Services.Validation;
using System.Reflection;
// using Microsoft.AspNetCore.Authentication.JwtBearer; // COMMENTED OUT - Auth disabled
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
// using Microsoft.IdentityModel.Tokens; // COMMENTED OUT - Auth disabled
// using System.Text; // COMMENTED OUT - Auth disabled


var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
        policy.WithOrigins(
            "http://localhost:5173",
            "http://localhost:5174",
            "http://localhost:5175",
            "https://mapping-frontend-ns99h3.azurewebsites.net")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddSingleton<ITextNormalizer, TextNormalizer>();
builder.Services.AddScoped<ILLMValidator, LLMValidator>();
builder.Services.AddScoped<IAreaMapperService, AreaMapperService>();
builder.Services.AddScoped<IReviewService, ReviewService>();
builder.Services.AddSingleton<ICompetenceMappingQueue, CompetenceMappingQueue>();
builder.Services.AddHostedService<CompetenceMappingWorker>();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath);
    }
});


// auth is disabled for the current internal demo setup.
// Re-enable the JWT block, AddAuthorization, and middleware below together with
// the frontend token interceptor before exposing write endpoints to untrusted users.
// JWT Authentication - COMMENTED OUT
// var jwtSecretKey = builder.Configuration["Jwt:SecretKey"]
//     ?? throw new InvalidOperationException("JWT SecretKey is not configured in appsettings.json");
// var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "MappingLIA";
// var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "MappingLIA";

// builder.Services.AddAuthentication(options =>
// {
//     options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
//     options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
// })
// .AddJwtBearer(options =>
// {
//     options.TokenValidationParameters = new TokenValidationParameters
//     {
//         ValidateIssuer = true,
//         ValidateAudience = true,
//         ValidateLifetime = true,
//         ValidateIssuerSigningKey = true,
//         ValidIssuer = jwtIssuer,
//         ValidAudience = jwtAudience,
//         IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecretKey)),
//         ClockSkew = TimeSpan.Zero
//     };
// });

// builder.Services.AddAuthorization();

var app = builder.Build();

// Initialize database on startup: seed areas/categories/subcategories, then transfer legacy competences
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var normalizer = scope.ServiceProvider.GetRequiredService<ITextNormalizer>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    DbInitializer.SeedData(context);
    await DbInitializer.MigrateLegacyCompetencesAsync(context, logger);
    // DbInitializer.SeedAdminUser(context); // COMMENTED OUT - Auth disabled
    await DbInitializer.TransferImportCompetencesAsync(context, normalizer, logger);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseCors("Frontend");

// app.UseAuthentication(); // COMMENTED OUT - Auth disabled
// app.UseAuthorization(); // COMMENTED OUT - Auth disabled

app.MapControllers();

app.Run();
// AUTH CONTROLLER - COMMENTED OUT - Auth disabled
// using System.ComponentModel.DataAnnotations;
// using System.IdentityModel.Tokens.Jwt;
// using System.Security.Claims;
// using System.Text;
using Microsoft.AspNetCore.Mvc; // Still needed for [ApiController], [Route], ActionResult
// using Microsoft.EntityFrameworkCore;
// using Microsoft.IdentityModel.Tokens;
// using Mapping_LIA.Data;

namespace Mapping_LIA.Controllers;

/// <summary>
/// Temporary authentication shim used without active login enforcement.
/// </summary>
/// <remarks>
/// The real JWT login implementation is preserved in comments,
/// but this controller currently returns a dummy token so the frontend can keep
/// its existing login call shape. Do not treat this token as authorization.
/// </remarks>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    // private readonly ApplicationDbContext _context;
    // private readonly IConfiguration _configuration;

    // public AuthController(ApplicationDbContext context, IConfiguration configuration)
    // {
    //     _context = context;
    //     _configuration = configuration;
    // }

    // public record LoginRequest(
    //     [Required] string Username,
    //     [Required] string Password
    // );

    // [HttpPost("login")]
    // public async Task<ActionResult> Login([FromBody] LoginRequest request, CancellationToken ct = default)
    // {
    //     if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
    //     {
    //         return BadRequest(new { message = "Username and password are required." });
    //     }

    //     var user = await _context.Users
    //         .FirstOrDefaultAsync(u => u.Username == request.Username, ct);

    //     if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
    //     {
    //         return Unauthorized(new { message = "Invalid username or password." });
    //     }

    //     var token = GenerateJwtToken(user);

    //     return Ok(new { token, username = user.Username });
    // }

    // private string GenerateJwtToken(Entities.User user)
    // {
    //     var secretKey = _configuration["Jwt:SecretKey"]
    //         ?? throw new InvalidOperationException("JWT SecretKey is not configured.");

    //     var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
    //     var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

    //     var claims = new[]
    //     {
    //         new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString()),
    //         new Claim(ClaimTypes.Name, user.Username),
    //     };

    //     var token = new JwtSecurityToken(
    //         issuer: _configuration["Jwt:Issuer"] ?? "MappingLIA",
    //         audience: _configuration["Jwt:Audience"] ?? "MappingLIA",
    //         claims: claims,
    //         expires: DateTime.UtcNow.AddHours(24),
    //         signingCredentials: credentials
    //     );

    //     return new JwtSecurityTokenHandler().WriteToken(token);
    // }

    /// <summary>
    /// Returns a fixed guest identity while authentication is disabled.
    /// </summary>
    /// <remarks>
    /// This endpoint is compatibility glue for the current UI, not a security
    /// boundary. Re-enable the commented JWT path before exposing the API to
    /// users who should not have review/import/delete permissions.
    /// </remarks>
    [HttpPost("login")]
    public ActionResult Login()
    {
        return Ok(new { token = "dummy-token", username = "guest" });
    }
}

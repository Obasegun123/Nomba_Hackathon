using System.Security.Cryptography;
using System.Text;

namespace Nomba_Hackathon.Service;

// Minimal API-key gate for the read endpoints. If no "Security:ApiKey" is
// configured the check is skipped so the API works out-of-the-box for demos.
public class ApiKeyEndpointFilter : IEndpointFilter
{
    public const string HeaderName = "X-Api-Key";

    private readonly string? _apiKey;

    public ApiKeyEndpointFilter(IConfiguration config)
    {
        _apiKey = config["Security:ApiKey"];
    }

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!string.IsNullOrEmpty(_apiKey))
        {
            var provided = context.HttpContext.Request.Headers[HeaderName].ToString();
            var matches = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(provided),
                Encoding.UTF8.GetBytes(_apiKey));

            if (!matches)
            {
                return Results.Unauthorized();
            }
        }

        return await next(context);
    }
}

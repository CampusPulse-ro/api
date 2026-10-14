using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace CampusPulse.Api.Auth;

/// <summary>
/// Describes sign-in in the OpenAPI contract: a bearer (JWT) security scheme, required by every
/// operation except those marked <c>AllowAnonymous</c> (the same rule as the fallback policy), so the
/// generated clients know which calls need the Microsoft token.
/// </summary>
public static class OpenApiBearerSecurity
{
    public const string SchemeName = "Bearer";

    public static OpenApiOptions AddBearerSecurity(this OpenApiOptions options)
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "A Microsoft access token for a UPT account, for this API's access_as_user scope.",
            };
            return Task.CompletedTask;
        });

        options.AddOperationTransformer((operation, context, _) =>
        {
            if (!context.Description.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
            {
                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(SchemeName, context.Document)] = [],
                });
            }

            return Task.CompletedTask;
        });

        return options;
    }
}

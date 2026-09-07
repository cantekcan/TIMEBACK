using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Timeback.Application.Common;
using Timeback.Domain.Common;

namespace Timeback.Api;

/// <summary>Maps domain / application / validation exceptions to RFC 7807 responses. Never leaks internals.</summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problems, ILogger<ApiExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        var (status, title, detail) = ex switch
        {
            ValidationException v => (StatusCodes.Status400BadRequest, "Validation failed",
                string.Join(" ", v.Errors.Select(e => e.ErrorMessage))),
            DomainException d => (StatusCodes.Status422UnprocessableEntity, "Rule violation", d.Message),
            NotFoundException n => (StatusCodes.Status404NotFound, "Not found", n.Message),
            ConcurrencyConflictException cc => (StatusCodes.Status409Conflict, "Concurrent modification", cc.Message),
            ConflictException c => (StatusCodes.Status409Conflict, "Conflict", c.Message),
            _ => (StatusCodes.Status500InternalServerError, "Server error", "An unexpected error occurred."),
        };

        if (status == StatusCodes.Status500InternalServerError)
            logger.LogError(ex, "Unhandled exception");
        else
            logger.LogInformation("Handled {Type}: {Message}", ex.GetType().Name, ex.Message);

        ctx.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = ctx,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Detail = detail }
        });
    }
}

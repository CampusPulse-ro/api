using System.Security.Claims;
using CampusPulse.Api.Http;
using CampusPulse.Api.Users;
using CampusPulse.Domain.Deadlines;
using CampusPulse.Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CampusPulse.Api.Deadlines;

/// <summary>A deadline the student entered: <see cref="DueAt"/> is an instant (UTC), <see cref="Course"/> and
/// <see cref="Notes"/> null when not given.</summary>
public sealed record DeadlineResponse(Guid Id, string Title, string? Course, DateTimeOffset DueAt, string? Notes, bool Done,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    internal static DeadlineResponse Of(Deadline d) => new(d.Id, d.Title, d.Course, d.DueAt, d.Notes, d.Done, d.CreatedAt, d.UpdatedAt);
}

/// <summary>Body of <c>POST /me/deadlines</c>: a title of 1-200 characters, an optional course (at most 200) and notes
/// (at most 2000), and when it is due.</summary>
public sealed record CreateDeadlineRequest(string Title, DateTimeOffset DueAt, string? Course = null, string? Notes = null);

/// <summary>Body of <c>PATCH /me/deadlines/{id}</c>: only the fields to change; a missing field stays as it is.
/// <see cref="Title"/>, <see cref="DueAt"/> and <see cref="Done"/> can't be null (400). A null <see cref="Course"/> or
/// <see cref="Notes"/> stays as it is, and an empty one clears it.</summary>
public sealed record UpdateDeadlineRequest
{
    // Which fields the body had: the JSON reader calls an init accessor only for a property it found.
    private bool titleGiven;
    private bool dueAtGiven;
    private bool doneGiven;

    public string Title { get; init { field = value; titleGiven = true; } } = null!;

    public string? Course { get; init; }

    public DateTimeOffset DueAt { get; init { field = value; dueAtGiven = true; } }

    public string? Notes { get; init; }

    public bool Done { get; init { field = value; doneGiven = true; } }

    /// <summary>The body sets <see cref="Title"/> to null.</summary>
    internal bool NullTitle => titleGiven && Title is null;

    internal string? NewTitle => titleGiven ? Title : null;

    internal DateTimeOffset? NewDueAt => dueAtGiven ? DueAt : null;

    internal bool? NewDone => doneGiven ? Done : null;
}

/// <summary>
/// The student's own deadlines (CampusPulse-ro/api#63), for students without UPT linked and for anything Campus Virtual
/// doesn't have. At most <see cref="Deadline.MaxPerUser"/> per student. Every endpoint needs an account (404 like
/// <c>GET /me</c> otherwise), and a deadline id of someone else's is treated as unknown.
/// </summary>
public static class DeadlineEndpoints
{
    public static IEndpointRouteBuilder MapDeadlines(this IEndpointRouteBuilder app)
    {
        var deadlines = app.MapGroup("/me/deadlines").RequireAuthorization().WithTags("Deadlines");

        deadlines.MapGet("", List)
            .WithName("GetDeadlines")
            .WithHttpCaching(HttpCacheScope.PerStudent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        deadlines.MapPost("", Create)
            .WithName("CreateDeadline")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized);

        deadlines.MapPatch("/{id:guid}", Update)
            .WithName("UpdateDeadline")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        deadlines.MapDelete("/{id:guid}", Delete)
            .WithName("DeleteDeadline")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }

    /// <summary>The student's deadlines due from <paramref name="from"/> to <paramref name="to"/> (both optional and
    /// inclusive), by due time.</summary>
    internal static async Task<Results<Ok<IReadOnlyList<DeadlineResponse>>, ProblemHttpResult>> List(DateTimeOffset? from,
        DateTimeOffset? to, ClaimsPrincipal principal, CampusPulseDbContext db, CancellationToken cancellationToken)
    {
        if (await Caller.IdAsync(db, principal, cancellationToken) is not { } userId)
        {
            return MeEndpoint.NoAccount();
        }

        var query = db.Deadlines.AsNoTracking().Where(d => d.UserId == userId);
        if (from is { } start)
        {
            query = query.Where(d => d.DueAt >= start.ToUniversalTime());
        }

        if (to is { } end)
        {
            query = query.Where(d => d.DueAt <= end.ToUniversalTime());
        }

        IReadOnlyList<DeadlineResponse> body = [.. (await query.OrderBy(d => d.DueAt).ThenBy(d => d.Id).ToListAsync(cancellationToken))
            .Select(DeadlineResponse.Of)];
        return TypedResults.Ok(body);
    }

    /// <summary>Adds a deadline: 201. 400 <c>invalid-deadline</c> for a title, course, notes or due time out of bounds
    /// (a body without <c>dueAt</c> doesn't match the contract: 400 <c>invalid-request</c>); 409
    /// <c>deadline-limit</c> at <see cref="Deadline.MaxPerUser"/> deadlines.</summary>
    internal static async Task<Results<Created<DeadlineResponse>, ProblemHttpResult>> Create(CreateDeadlineRequest request,
        ClaimsPrincipal principal, CampusPulseDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (Deadline.Problem(request.Title, request.Course, request.Notes, request.DueAt) is { } problem)
        {
            return InvalidDeadline(problem);
        }

        if (await Caller.IdAsync(db, principal, cancellationToken) is not { } userId)
        {
            return MeEndpoint.NoAccount();
        }

        // The student's row, locked: two adds at once can't both pass the limit, and a deleted account is a 404.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if ((await db.Users.FromSql($"SELECT * FROM users WHERE id = {userId} FOR UPDATE").ToListAsync(cancellationToken)).Count == 0)
        {
            return MeEndpoint.NoAccount();
        }

        if (await db.Deadlines.CountAsync(d => d.UserId == userId, cancellationToken) >= Deadline.MaxPerUser)
        {
            return Problems.Of(StatusCodes.Status409Conflict, "deadline-limit", "Too many deadlines",
                $"A student can keep at most {Deadline.MaxPerUser} deadlines: delete some first.");
        }

        var deadline = new Deadline(userId, request.Title, request.Course, request.DueAt, request.Notes, time.GetUtcNow());
        db.Deadlines.Add(deadline);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Created($"/me/deadlines/{deadline.Id}", DeadlineResponse.Of(deadline));
    }

    /// <summary>Changes the given fields of one of the student's deadlines: 200 with the result; 404 for an unknown id;
    /// 400 <c>invalid-deadline</c> as for adding.</summary>
    internal static async Task<Results<Ok<DeadlineResponse>, ProblemHttpResult>> Update(Guid id, UpdateDeadlineRequest request,
        ClaimsPrincipal principal, CampusPulseDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (await Caller.IdAsync(db, principal, cancellationToken) is not { } userId)
        {
            return MeEndpoint.NoAccount();
        }

        var deadline = await db.Deadlines.SingleOrDefaultAsync(d => d.Id == id && d.UserId == userId, cancellationToken);
        if (deadline is null)
        {
            return NoDeadline();
        }

        if (request.NullTitle)
        {
            return InvalidDeadline("title can't be null: leave it out to keep it.");
        }

        var title = request.NewTitle ?? deadline.Title;
        var course = request.Course ?? deadline.Course;
        var notes = request.Notes ?? deadline.Notes;
        var dueAt = request.NewDueAt ?? deadline.DueAt;
        if (Deadline.Problem(title, course, notes, dueAt) is { } problem)
        {
            return InvalidDeadline(problem);
        }

        deadline.Update(title, course, dueAt, notes, request.NewDone ?? deadline.Done, time.GetUtcNow());
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return NoDeadline(); // deleted meanwhile
        }

        return TypedResults.Ok(DeadlineResponse.Of(deadline));
    }

    /// <summary>Deletes one of the student's deadlines: 204, also when there was nothing to delete.</summary>
    internal static async Task<Results<NoContent, ProblemHttpResult>> Delete(Guid id, ClaimsPrincipal principal, CampusPulseDbContext db,
        CancellationToken cancellationToken)
    {
        if (await Caller.IdAsync(db, principal, cancellationToken) is not { } userId)
        {
            return MeEndpoint.NoAccount();
        }

        await db.Deadlines.Where(d => d.Id == id && d.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static ProblemHttpResult InvalidDeadline(string detail) =>
        Problems.Of(StatusCodes.Status400BadRequest, "invalid-deadline", "Not a valid deadline", detail);

    private static ProblemHttpResult NoDeadline() => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such deadline");
}

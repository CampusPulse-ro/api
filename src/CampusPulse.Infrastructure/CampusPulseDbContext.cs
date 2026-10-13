using Microsoft.EntityFrameworkCore;

namespace CampusPulse.Infrastructure;

/// <summary>No tables yet. Entities come from CampusPulse.Domain; their mappings go here.</summary>
public sealed class CampusPulseDbContext(DbContextOptions<CampusPulseDbContext> options)
    : DbContext(options);

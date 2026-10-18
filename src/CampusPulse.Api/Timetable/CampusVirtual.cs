namespace CampusPulse.Api.Timetable;

/// <summary>Campus Virtual (cv.upt.ro), UPT's Moodle: where a subject's course page is.</summary>
public static class CampusVirtual
{
    public const string BaseUrl = "https://cv.upt.ro";

    /// <summary>The course's page, or null for a subject without a known course.</summary>
    public static string? CourseUrl(int? courseId) => courseId is { } id ? $"{BaseUrl}/course/view.php?id={id}" : null;
}

using Raavi.Domain.Common;

namespace Raavi.Wiki.Domain;

public enum CalendarScope
{
    Personal = 1,
    Space = 2,
    Organization = 3,
}

public enum CalendarGrant
{
    FreeBusy = 1,
    View = 2,
    Create = 3,
    EditOwn = 4,
    EditAll = 5,
    Manage = 6,
}

public enum CalendarSubjectType
{
    User = 1,
    Group = 2,
}

public enum CalendarEventType
{
    Event = 0,
    Meeting = 1,
    Reminder = 2,
    TaskDeadline = 3,
    Milestone = 4,
    Release = 5,
    Holiday = 6,
    Maintenance = 7,
    Training = 8,
    Appointment = 9,
    FocusTime = 10,
    OrganizationEvent = 11,
}

public enum CalendarEventStatus
{
    Scheduled = 0,
    Confirmed = 1,
    Tentative = 2,
    Cancelled = 3,
    Completed = 4,
}

public enum CalendarEventPriority
{
    Normal = 0,
    Low = 1,
    High = 2,
}

/// <summary>
/// A named calendar (Personal / Space / Organization). Space events from Phase 9b live on a default Space calendar created at migration.
/// Archive hides the calendar without deleting events; hard delete is an explicit service operation.
/// </summary>
public sealed class Calendar : Entity, ITenantOwned
{
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 2000;
    public const int MaxColorLength = 32;
    public const int MaxIconLength = 64;

    private Calendar() { }

    public Guid TenantId { get; private set; }

    public CalendarScope Scope { get; private set; }

    /// <summary>Set for Space calendars.</summary>
    public Guid? SpaceId { get; private set; }

    /// <summary>Set for Personal calendars (owner).</summary>
    public Guid? OwnerUserId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public string Color { get; private set; } = "#3b82f6";

    public string? Icon { get; private set; }

    public bool IsDefault { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    public Guid CreatedBy { get; private set; }

    public Guid UpdatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public int Revision { get; private set; } = 1;

    public static Calendar CreateSpaceDefault(Guid spaceId, string name, Guid userId, DateTimeOffset now) =>
        Create(CalendarScope.Space, spaceId, ownerUserId: null, name, description: null, color: "#3b82f6", icon: null, isDefault: true, userId, now);

    public static Calendar CreatePersonal(Guid userId, string? name, DateTimeOffset now) =>
        Create(CalendarScope.Personal, spaceId: null, ownerUserId: userId, string.IsNullOrWhiteSpace(name) ? "Personal" : name, null, "#10b981", null, isDefault: true, userId, now);

    public static Calendar CreateOrganization(string name, string? description, string? color, Guid userId, DateTimeOffset now) =>
        Create(CalendarScope.Organization, spaceId: null, ownerUserId: null, name, description, color ?? "#8b5cf6", null, isDefault: false, userId, now);

    public static Calendar CreateSpace(Guid spaceId, string name, string? description, string? color, Guid userId, DateTimeOffset now) =>
        Create(CalendarScope.Space, spaceId, null, name, description, color ?? "#3b82f6", null, isDefault: false, userId, now);

    private static Calendar Create(CalendarScope scope, Guid? spaceId, Guid? ownerUserId, string? name, string? description, string color, string? icon, bool isDefault, Guid userId, DateTimeOffset now)
    {
        var cal = new Calendar
        {
            Scope = scope,
            SpaceId = spaceId,
            OwnerUserId = ownerUserId,
            IsDefault = isDefault,
            CreatedBy = userId,
            UpdatedBy = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        cal.SetMeta(name, description, color, icon);
        if (scope == CalendarScope.Space && spaceId is null)
        {
            throw new DomainException("calendar.space_required", "A space calendar needs a space.");
        }

        if (scope == CalendarScope.Personal && ownerUserId is null)
        {
            throw new DomainException("calendar.owner_required", "A personal calendar needs an owner.");
        }

        return cal;
    }

    public void UpdateMeta(string? name, string? description, string? color, string? icon, Guid userId, DateTimeOffset now)
    {
        SetMeta(name ?? Name, description, color ?? Color, icon);
        UpdatedBy = userId;
        UpdatedAt = now;
        Revision++;
    }

    public void Archive(Guid userId, DateTimeOffset now)
    {
        if (ArchivedAt is not null)
        {
            return;
        }

        ArchivedAt = now;
        UpdatedBy = userId;
        UpdatedAt = now;
        Revision++;
    }

    public void Restore(Guid userId, DateTimeOffset now)
    {
        if (ArchivedAt is null)
        {
            return;
        }

        ArchivedAt = null;
        UpdatedBy = userId;
        UpdatedAt = now;
        Revision++;
    }

    private void SetMeta(string? name, string? description, string? color, string? icon)
    {
        var n = (name ?? string.Empty).Trim();
        if (n.Length is 0 or > MaxNameLength || n.Any(char.IsControl))
        {
            throw new DomainException("calendar.name_invalid", $"A calendar needs a name of 1–{MaxNameLength} characters.");
        }

        var d = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (d is { Length: > MaxDescriptionLength })
        {
            throw new DomainException("calendar.description_too_long", $"Description is limited to {MaxDescriptionLength} characters.");
        }

        var c = string.IsNullOrWhiteSpace(color) ? "#3b82f6" : color.Trim().ToLowerInvariant();
        if (c.Length > MaxColorLength || !(c == "transparent" || System.Text.RegularExpressions.Regex.IsMatch(c, "^#[0-9a-f]{6}$")))
        {
            throw new DomainException("calendar.color_invalid", "Colour must look like #rrggbb.");
        }

        var i = string.IsNullOrWhiteSpace(icon) ? null : icon.Trim();
        if (i is { Length: > MaxIconLength } || (i?.Any(char.IsControl) ?? false))
        {
            throw new DomainException("calendar.icon_invalid", $"Icon is limited to {MaxIconLength} characters.");
        }

        Name = n;
        Description = d;
        Color = c;
        Icon = i;
    }
}

/// <summary>Share grant on a calendar (Personal Full/FreeBusy and Org/Space extras). Backend-enforced.</summary>
public sealed class CalendarAccessGrant : Entity, ITenantOwned
{
    private CalendarAccessGrant() { }

    public Guid TenantId { get; private set; }

    public Guid CalendarId { get; private set; }

    public CalendarSubjectType SubjectType { get; private set; }

    public Guid SubjectId { get; private set; }

    public CalendarGrant Grant { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static CalendarAccessGrant Create(Guid calendarId, CalendarSubjectType subjectType, Guid subjectId, CalendarGrant grant, Guid userId, DateTimeOffset now)
    {
        if (!Enum.IsDefined(subjectType) || !Enum.IsDefined(grant) || subjectId == Guid.Empty)
        {
            throw new DomainException("calendar.permission_invalid", "The calendar permission is not valid.");
        }

        return new CalendarAccessGrant
        {
            CalendarId = calendarId,
            SubjectType = subjectType,
            SubjectId = subjectId,
            Grant = grant,
            CreatedBy = userId,
            CreatedAt = now,
        };
    }

    public void ChangeGrant(CalendarGrant grant)
    {
        if (!Enum.IsDefined(grant))
        {
            throw new DomainException("calendar.permission_invalid", "The calendar permission is not valid.");
        }

        Grant = grant;
    }
}

public enum RecurrenceEditScope
{
    EntireSeries = 0,
    ThisOccurrence = 1,
    ThisAndFollowing = 2,
}

public enum CalendarAttendeeResponse
{
    Pending = 0,
    Accepted = 1,
    Declined = 2,
    Tentative = 3,
}

/// <summary>A invited workspace user on a calendar event (RSVP).</summary>
public sealed class CalendarEventAttendee : Entity, ITenantOwned
{
    public const int MaxPerEvent = 50;

    private CalendarEventAttendee() { }

    public Guid TenantId { get; private set; }

    public Guid EventId { get; private set; }

    public Guid UserId { get; private set; }

    public CalendarAttendeeResponse Response { get; private set; }

    public bool Optional { get; private set; }

    public DateTimeOffset InvitedAt { get; private set; }

    public DateTimeOffset? RespondedAt { get; private set; }

    public static CalendarEventAttendee Invite(Guid eventId, Guid userId, bool optional, DateTimeOffset now) => new()
    {
        EventId = eventId,
        UserId = userId,
        Optional = optional,
        Response = CalendarAttendeeResponse.Pending,
        InvitedAt = now,
    };

    public void Respond(CalendarAttendeeResponse response, DateTimeOffset now)
    {
        if (!Enum.IsDefined(response) || response == CalendarAttendeeResponse.Pending)
        {
            throw new DomainException("calendar.rsvp_invalid", "Choose Accepted, Declined or Tentative.");
        }

        Response = response;
        RespondedAt = now;
    }
}

/// <summary>Reminder offset before an event's start (minutes). Delivered by the calendar reminder job.</summary>
public sealed class CalendarEventReminder : Entity, ITenantOwned
{
    public const int MaxOffsetsPerEvent = 5;

    private CalendarEventReminder() { }

    public Guid TenantId { get; private set; }

    public Guid EventId { get; private set; }

    /// <summary>Minutes before start (0 = at start). Positive values only.</summary>
    public int MinutesBefore { get; private set; }

    public bool InApp { get; private set; } = true;

    public bool Email { get; private set; }

    /// <summary>Idempotency: last occurrence date for which this reminder fired (yyyy-MM-dd) or empty.</summary>
    public string? LastDeliveredOccurrence { get; private set; }

    public DateTimeOffset? LastDeliveredAt { get; private set; }

    public static CalendarEventReminder Create(Guid eventId, int minutesBefore, bool inApp, bool email)
    {
        if (minutesBefore is < 0 or > 60 * 24 * 30)
        {
            throw new DomainException("calendar.reminder_invalid", "Reminder offset must be between 0 and 30 days.");
        }

        if (!inApp && !email)
        {
            throw new DomainException("calendar.reminder_invalid", "Choose in-app and/or e-mail for the reminder.");
        }

        return new CalendarEventReminder
        {
            EventId = eventId,
            MinutesBefore = minutesBefore,
            InApp = inApp,
            Email = email,
        };
    }

    public void MarkDelivered(string occurrenceDate, DateTimeOffset now)
    {
        LastDeliveredOccurrence = occurrenceDate;
        LastDeliveredAt = now;
    }
}

/// <summary>Per-user calendar notification preferences (Quiet Hours, TTS, privacy).</summary>
public sealed class CalendarUserPreference : Entity, ITenantOwned
{
    private CalendarUserPreference() { }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public bool BrowserAlerts { get; private set; } = true;

    public bool TtsEnabled { get; private set; }

    public string TtsLocale { get; private set; } = "en";

    public double TtsRate { get; private set; } = 1.0;

    /// <summary>When true, TTS speaks a generic phrase instead of the event title for private events.</summary>
    public bool HidePrivateTitlesInTts { get; private set; } = true;

    /// <summary>Quiet hours start (inclusive), wall-clock minutes from midnight local; null = off.</summary>
    public int? QuietStartMinutes { get; private set; }

    public int? QuietEndMinutes { get; private set; }

    public string TimeZone { get; private set; } = "UTC";

    public DateTimeOffset UpdatedAt { get; private set; }

    public static CalendarUserPreference Create(Guid userId, DateTimeOffset now) => new()
    {
        UserId = userId,
        UpdatedAt = now,
    };

    public void Update(bool browserAlerts, bool ttsEnabled, string? ttsLocale, double ttsRate, bool hidePrivateTitles, int? quietStart, int? quietEnd, string? timeZone, DateTimeOffset now)
    {
        if (ttsRate is < 0.5 or > 2.0)
        {
            throw new DomainException("calendar.prefs_invalid", "Speech rate must be between 0.5 and 2.");
        }

        if ((quietStart is null) != (quietEnd is null))
        {
            throw new DomainException("calendar.prefs_invalid", "Quiet hours need both a start and an end, or neither.");
        }

        if (quietStart is { } qs && (qs is < 0 or > 1439 || quietEnd is < 0 or > 1439))
        {
            throw new DomainException("calendar.prefs_invalid", "Quiet hours must be minutes from midnight (0–1439).");
        }

        var zone = string.IsNullOrWhiteSpace(timeZone) ? "UTC" : timeZone.Trim();
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _))
        {
            throw new DomainException("calendar.time_zone_invalid", "The time zone is not recognised.");
        }

        var locale = string.IsNullOrWhiteSpace(ttsLocale) ? "en" : ttsLocale.Trim().ToLowerInvariant();
        if (locale is not ("en" or "fa"))
        {
            throw new DomainException("calendar.prefs_invalid", "TTS locale must be en or fa.");
        }

        BrowserAlerts = browserAlerts;
        TtsEnabled = ttsEnabled;
        TtsLocale = locale;
        TtsRate = ttsRate;
        HidePrivateTitlesInTts = hidePrivateTitles;
        QuietStartMinutes = quietStart;
        QuietEndMinutes = quietEnd;
        TimeZone = zone;
        UpdatedAt = now;
    }

    public bool IsQuiet(DateTimeOffset utcNow)
    {
        if (QuietStartMinutes is not { } start || QuietEndMinutes is not { } end)
        {
            return false;
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
        var local = TimeZoneInfo.ConvertTime(utcNow, zone);
        var minutes = local.Hour * 60 + local.Minute;
        return start <= end ? minutes >= start && minutes < end : minutes >= start || minutes < end;
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Application.Abstractions.Content;
using Raavi.Infrastructure.Messaging;
using Raavi.Infrastructure.Persistence;
using Raavi.Wiki.Application;
using Raavi.Wiki.ContentTypes;
using Raavi.Wiki.Persistence;
using Raavi.Wiki.Security;

namespace Raavi.Wiki;

public static class WikiModule
{
    /// <summary>
    /// Wiki core: spaces, content tree, permissions, templates, activity and its outbox. Requires an
    /// <c>IPrincipalDirectory</c> and <c>ICurrentUser</c> (Identity). <paramref name="runOutboxPoller"/> only in the worker host.
    /// </summary>
    public static IServiceCollection AddWikiModule(this IServiceCollection services, IConfiguration configuration, bool runOutboxPoller = false)
    {
        services.AddRaaviDbContext<WikiDbContext>(configuration, WikiDbContext.HistorySchema);
        services.AddRaaviOutbox<WikiDbContext>(runOutboxPoller);

        services.AddScoped<IContentProvider, PageContentProvider>();
        services.AddScoped<IContentProvider, FolderContentProvider>();
        services.AddScoped<ContentProviderRegistry>();
        services.AddScoped<IContentProviderRegistry>(sp => sp.GetRequiredService<ContentProviderRegistry>());

        services.AddScoped<WikiActor>();
        services.AddScoped<WikiAuthorizer>();
        services.AddScoped<IPermissionEvaluator>(sp => sp.GetRequiredService<WikiAuthorizer>());
        services.AddScoped<SpaceService>();
        services.AddScoped<PageService>();
        services.AddScoped<Raavi.Application.Abstractions.Ai.IPageTextReader, PageTextReader>();
        services.AddScoped<DiscoveryService>();
        services.AddScoped<TemplateService>();
        services.AddScoped<AttachmentService>();
        services.AddScoped<EditorService>();
        services.AddScoped<PublishingService>();
        services.AddScoped<ScheduledPublishJob>();
        services.AddSingleton<Raavi.Wiki.Editor.DocumentHtmlRenderer>();
        services.AddScoped<IOutboxHandler, ActivityProjection>();
        services.AddScoped<Raavi.Application.Abstractions.Content.IContentImporter, ContentImporter>();

        // Comments, watching and notifications (Phase 7a). Push delivery (SignalR) is added by the API host: AddWikiRealtime().
        services.AddScoped<CommentService>();
        services.AddScoped<TaskService>();
        services.AddScoped<CalendarService>();
        services.AddScoped<DatabaseService>();
        services.AddScoped<SlideService>();
        services.AddScoped<WhiteboardService>();
        services.AddScoped<WorkflowService>();
        services.AddScoped<ApprovalService>();
        services.AddScoped<IOutboxHandler, ApprovalNotifier>();
        services.AddScoped<WorkflowEngine>();
        services.AddScoped<IOutboxHandler>(sp => sp.GetRequiredService<WorkflowEngine>());
        services.AddScoped<StaleSweep>();
        services.AddScoped<NotificationService>();
        services.AddScoped<IOutboxHandler, NotificationFanOut>();
        services.AddScoped<IOutboxHandler, NotificationEmailer>();

        // Real-time collaboration (Phase 6): tickets, internal persistence, revocation.
        services.AddHttpClient(Raavi.Wiki.Collaboration.CollaborationClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<Raavi.Wiki.Collaboration.TicketSigner>();
        services.AddScoped<Raavi.Wiki.Collaboration.CollaborationClient>();
        services.AddScoped<Raavi.Application.Abstractions.Collaboration.ICollaborationRevocation>(sp => sp.GetRequiredService<Raavi.Wiki.Collaboration.CollaborationClient>());
        services.AddScoped<Raavi.Wiki.Collaboration.CollaborationService>();
        services.AddScoped<IOutboxHandler, Raavi.Wiki.Api.CollaborationRevocationHandler>();
        return services;
    }
}

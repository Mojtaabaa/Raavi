using Microsoft.Extensions.Options;
using Raavi.Application.Abstractions.Email;
using Raavi.Application.Abstractions.Identity;
using Raavi.Infrastructure.Email;
using Raavi.Wiki.Application;
using Raavi.Wiki.Domain;

namespace Raavi.Tests.Wiki;

public sealed class NotificationEmailTests
{
    [Fact]
    public void Templates_are_localized_and_encode_every_user_value()
    {
        var n = Notification.Create(Guid.NewGuid(), Guid.NewGuid(), NotificationKinds.Mention, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "<script>alert(1)</script>", "see <b>this</b> & that", email: true, DateTimeOffset.UtcNow);
        var link = new Uri("https://acme.raavi.test/spaces/ENG/pages/x#comment-y");

        var fa = NotificationEmailTemplate.Build(n, new PrincipalContact(Guid.NewGuid(), "سارا", "sara@acme.test", "fa"), "علی <x>", link);
        Assert.Contains("از شما نام برد", fa.Subject, StringComparison.Ordinal);
        Assert.Contains("dir=\"rtl\"", fa.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", fa.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>this</b>", fa.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", fa.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(link.AbsoluteUri, fa.TextBody, StringComparison.Ordinal);

        var en = NotificationEmailTemplate.Build(n, new PrincipalContact(Guid.NewGuid(), "Sara", "sara@acme.test", "en"), "Ali", link);
        Assert.StartsWith("Ali mentioned you", en.Subject, StringComparison.Ordinal);
        Assert.Equal("sara@acme.test", en.To);
    }

    [Fact]
    public async Task Pickup_mode_writes_rfc822_files_and_disabled_mode_sends_nothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "raavi-mail-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sender = new MailKitEmailSender(Options.Create(new EmailOptions { Mode = EmailMode.Pickup, FromAddress = "no-reply@raavi.test", PickupDirectory = dir }));
            Assert.True(sender.IsEnabled);
            await sender.SendAsync(new EmailMessage("sara@acme.test", "Sara", "Hello", "text", "<p>html</p>"));
            var file = Assert.Single(Directory.GetFiles(dir, "*.eml"));
            var raw = await File.ReadAllTextAsync(file);
            Assert.Contains("To: Sara <sara@acme.test>", raw, StringComparison.Ordinal);
            Assert.Contains("Auto-Submitted: auto-generated", raw, StringComparison.Ordinal);

            var disabled = new MailKitEmailSender(Options.Create(new EmailOptions()));
            Assert.False(disabled.IsEnabled);
            await disabled.SendAsync(new EmailMessage("a@b.c", "A", "s", "t", "h"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Invalid_email_configuration_is_rejected_at_startup()
    {
        Assert.Throws<OptionsValidationException>(() => new EmailOptions { Mode = EmailMode.Smtp, FromAddress = "no-reply@raavi.test" }.Validate());
        Assert.Throws<OptionsValidationException>(() => new EmailOptions { Mode = EmailMode.Pickup, FromAddress = "nope" }.Validate());
        new EmailOptions { Mode = EmailMode.Smtp, FromAddress = "no-reply@raavi.test", Smtp = { Host = "smtp.internal" } }.Validate();
    }

    [Fact]
    public void Mentions_are_extracted_from_comment_bodies()
    {
        var id = Guid.NewGuid();
        var body = RaaviDocument.Parse($$$"""{"schema":"raavi.doc","schemaVersion":2,"doc":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"mention","attrs":{"id":"{{{id}}}","label":"Ali","mentionSuggestionChar":"@"}},{"type":"text","text":" hi"},{"type":"mention","attrs":{"id":"{{{id}}}","label":"Ali","mentionSuggestionChar":"@"}}]}]}}""");
        Assert.Equal([id], Comment.MentionedUsers(body));
        var c = Comment.StartThread(Guid.NewGuid(), CommentKind.Page, null, body, 1, Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Equal(c.Id, c.ThreadId);
        Assert.Throws<Raavi.Domain.Common.DomainException>(() => Comment.StartThread(Guid.NewGuid(), CommentKind.Inline, null, body, 1, Guid.NewGuid(), DateTimeOffset.UtcNow));
    }
}

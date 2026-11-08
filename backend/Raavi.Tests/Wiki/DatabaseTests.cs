using System.Net;
using System.Text.Json;
using Raavi.Domain.Common;
using Raavi.Tests.Identity;
using Raavi.Tests.Infrastructure;
using Raavi.Wiki.Domain;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Wiki;

/// <summary>Phase 9c: schema rules and value validation of space databases — no SQL needed.</summary>
public sealed class DatabaseDomainTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static SpaceDatabase Make(params DbColumnInput[] cols) => SpaceDatabase.Create(Guid.NewGuid(), "Projects", null, cols, Guid.NewGuid(), Now);

    private static DbColumnInput Col(string name, DbColumnType type, params string[] options) =>
        new(null, name, type, type == DbColumnType.Select ? options.Select(o => new DbOptionInput(null, o)).ToList() : null);

    private static Dictionary<string, JsonElement> Vals(params (string Id, string Json)[] v) => v.ToDictionary(x => x.Id, x => JsonDocument.Parse(x.Json).RootElement.Clone());

    private static string Code(Action a) => Assert.Throws<DomainException>(a).Code;

    [Fact]
    public void Schemas_get_stable_ids_unique_names_and_locked_types()
    {
        var d = Make(Col("Title", DbColumnType.Text), Col("Status", DbColumnType.Select, "Todo", "Done"));
        Assert.Equal(2, d.Columns.Count);
        Assert.All(d.Columns, c => Assert.StartsWith("c", c.Id, StringComparison.Ordinal));
        var status = d.Columns[1];
        Assert.Equal(["Todo", "Done"], status.Options.Select(o => o.Label));

        // Renaming keeps ids; existing options keep ids, new ones get new ids.
        d.Update("Projects", null, [new(d.Columns[0].Id, "Name", DbColumnType.Text, null), new(status.Id, "State", DbColumnType.Select, [new(status.Options[0].Id, "To do"), new(null, "Blocked")])], Now);
        Assert.Equal(2, d.Revision);
        Assert.Equal(("Name", "State"), (d.Columns[0].Name, d.Columns[1].Name));
        Assert.Equal(status.Options[0].Id, d.Columns[1].Options[0].Id);
        Assert.Equal(["To do", "Blocked"], d.Columns[1].Options.Select(o => o.Label));

        Assert.Equal("db.column_type_locked", Code(() => d.Update("P", null, [new(d.Columns[0].Id, "Name", DbColumnType.Number, null)], Now)));
        Assert.Equal("db.column_unknown", Code(() => d.Update("P", null, [new("cdeadbeef", "X", DbColumnType.Text, null)], Now)));
        Assert.Equal("db.column_invalid", Code(() => Make(Col("A", DbColumnType.Text), Col("a", DbColumnType.Number))));
        Assert.Equal("db.column_invalid", Code(() => Make(Col(" ", DbColumnType.Text))));
        Assert.Equal("db.column_invalid", Code(() => Make(new DbColumnInput(null, "S", DbColumnType.Select, []))));
        Assert.Equal("db.column_invalid", Code(() => Make(new DbColumnInput(null, "S", DbColumnType.Select, [new(null, "x"), new(null, "X")]))));
        Assert.Equal("db.column_invalid", Code(() => Make(new DbColumnInput(null, "S", DbColumnType.Select, [new("cforged", "x")]))));
        Assert.Equal("db.too_many_columns", Code(() => Make(Enumerable.Range(0, 31).Select(i => Col($"C{i}", DbColumnType.Text)).ToArray())));
        Assert.Equal("db.name_invalid", Code(() => SpaceDatabase.Create(Guid.NewGuid(), " ", null, [], Guid.NewGuid(), Now)));
    }

    [Fact]
    public void Values_are_validated_per_column_type_and_empty_cells_are_dropped()
    {
        var d = Make(Col("T", DbColumnType.Text), Col("N", DbColumnType.Number), Col("D", DbColumnType.Date), Col("B", DbColumnType.Checkbox), Col("S", DbColumnType.Select, "A", "B"), Col("U", DbColumnType.Url));
        var c = d.Columns;
        var ok = DatabaseRow.Create(d.Id, c, Vals((c[0].Id, "\"hello\""), (c[1].Id, "12.5"), (c[2].Id, "\"2026-02-03\""), (c[3].Id, "false"), (c[4].Id, $"\"{c[4].Options[1].Id}\""), (c[5].Id, "\"https://example.com/x\"")), Guid.NewGuid(), Now);
        Assert.Equal(6, ok.Values.Count);
        Assert.Equal(12.5, ok.Values[c[1].Id].GetDouble());

        var empty = DatabaseRow.Create(d.Id, c, Vals((c[0].Id, "\"  \""), (c[1].Id, "null")), Guid.NewGuid(), Now);
        Assert.Empty(empty.Values);

        foreach (var (col, json) in new[] { (1, "\"12\""), (1, "1e400"), (2, "\"2026-13-01\""), (2, "\"03/02/2026\""), (3, "\"yes\""), (4, "\"nope\""), (5, "\"javascript:alert(1)\""), (5, "\"ftp://x\""), (0, "5") })
        {
            Assert.Equal("db.value_invalid", Code(() => DatabaseRow.Create(d.Id, c, Vals((c[col].Id, json)), Guid.NewGuid(), Now)));
        }

        Assert.Equal("db.column_unknown", Code(() => DatabaseRow.Create(d.Id, c, Vals(("cunknown", "\"x\"")), Guid.NewGuid(), Now)));

        // A removed column's id is refused on write (its old data is simply ignored).
        var dropped = c[0].Id;
        d.Update("Projects", null, c.Skip(1).Select(x => new DbColumnInput(x.Id, x.Name, x.Type, x.Options.Select(o => new DbOptionInput(o.Id, o.Label)).ToList())).ToList(), Now);
        Assert.Equal("db.column_unknown", Code(() => ok.Update(d.Columns, Vals((dropped, "\"x\"")), Now)));
    }
}

/// <summary>Phase 9c acceptance: databases have persistence, tenant ownership, space-based permissions, ordering/filtering, navigation data and CSV export.</summary>
[Collection(SqlTestGroup.Name)]
public sealed class DatabaseTests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    private static async Task<Guid> MeAsync(Browser b) => (await b.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();

    private static async Task<JsonElement> CreateDbAsync(Browser b, string key, object body)
    {
        using var r = await b.PostAsync($"api/v1/spaces/{key}/databases", body);
        return await ReadAsync(r);
    }

    private static string[] Ids(JsonElement db) => db.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("id").GetString()!).ToArray();

    private static async Task<JsonElement> AddRowAsync(Browser b, Guid db, object values)
    {
        using var r = await b.PostAsync($"api/v1/databases/{db}/rows", new { values });
        return await ReadAsync(r);
    }

    private static async Task<string[]> NamesAsync(Browser b, string url, string col) =>
        (await b.GetJsonAsync(url)).GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("values").TryGetProperty(col, out var v) ? v.GetString()! : "<empty>").ToArray();

    private static readonly object[] Schema =
    [
        new { name = "Name", type = "Text" },
        new { name = "Points", type = "Number" },
        new { name = "Status", type = "Select", options = new[] { new { label = "Todo" }, new { label = "Done" } } },
    ];

    [Fact]
    public async Task Databases_hold_validated_rows_that_can_be_sorted_filtered_paged_and_exported()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var database = await CreateDbAsync(admin, key, new { name = "Backlog", columns = Schema });
        var id = database.GetProperty("id").GetGuid();
        var cols = Ids(database);
        var done = database.GetProperty("columns")[2].GetProperty("options")[1].GetProperty("id").GetString()!;

        await AddRowAsync(admin, id, new Dictionary<string, object> { [cols[0]] = "Banana", [cols[1]] = 10, [cols[2]] = done });
        await AddRowAsync(admin, id, new Dictionary<string, object> { [cols[0]] = "apple", [cols[1]] = 9 });
        await AddRowAsync(admin, id, new Dictionary<string, object> { [cols[0]] = "=SUM(A1)", [cols[1]] = 100 });
        await AddRowAsync(admin, id, new Dictionary<string, object> { [cols[1]] = 2 }); // no name

        // Text sorts case-insensitively with empty cells last (also descending); numbers sort numerically, not as text.
        Assert.Equal(["=SUM(A1)", "apple", "Banana", "<empty>"], await NamesAsync(admin, $"api/v1/databases/{id}/rows?sort={cols[0]}&dir=asc", cols[0]));
        Assert.Equal(["Banana", "apple", "=SUM(A1)", "<empty>"], await NamesAsync(admin, $"api/v1/databases/{id}/rows?sort={cols[0]}&dir=desc", cols[0]));
        var byPoints = (await admin.GetJsonAsync($"api/v1/databases/{id}/rows?sort={cols[1]}&dir=asc")).GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("values").GetProperty(cols[1]).GetDouble()).ToArray();
        Assert.Equal([2d, 9d, 10d, 100d], byPoints);

        // Free-text filter looks at every cell, including a select's label.
        Assert.Equal(1, (await admin.GetJsonAsync($"api/v1/databases/{id}/rows?q=DONE")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await admin.GetJsonAsync($"api/v1/databases/{id}/rows?q=pp")).GetProperty("total").GetInt32()); // "apple"
        var page = await admin.GetJsonAsync($"api/v1/databases/{id}/rows?skip=1&take=2");
        Assert.Equal((4, 2), (page.GetProperty("total").GetInt32(), page.GetProperty("rows").GetArrayLength()));

        using (var badSort = await admin.GetAsync($"api/v1/databases/{id}/rows?sort=cnope"))
        {
            await Browser.AssertStatusAsync(badSort, HttpStatusCode.BadRequest, "db.sort_invalid");
        }

        // Invalid values and unknown columns are rejected with stable codes.
        foreach (var (values, code) in new (object, string)[]
        {
            (new Dictionary<string, object> { [cols[1]] = "ten" }, "db.value_invalid"),
            (new Dictionary<string, object> { [cols[2]] = "not-an-option" }, "db.value_invalid"),
            (new Dictionary<string, object> { ["cmissing"] = "x" }, "db.column_unknown"),
        })
        {
            using var r = await admin.PostAsync($"api/v1/databases/{id}/rows", new { values });
            await Browser.AssertStatusAsync(r, HttpStatusCode.BadRequest, code);
        }

        Assert.Equal(4, (await admin.GetJsonAsync($"api/v1/databases/{id}")).GetProperty("rowCount").GetInt32());

        using var export = await admin.GetAsync($"api/v1/databases/{id}/export");
        Assert.Equal("text/csv", export.Content.Headers.ContentType!.MediaType);
        var bytes = await export.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var csv = System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.StartsWith("Name,Points,Status\r\n", csv, StringComparison.Ordinal);
        Assert.Contains("Banana,10,Done\r\n", csv, StringComparison.Ordinal); // select shows its label
        Assert.Contains("'=SUM(A1),100,\r\n", csv, StringComparison.Ordinal); // formula neutralised
    }

    [Fact]
    public async Task Schema_and_row_updates_use_etags_keep_ids_and_deleting_cascades_to_rows()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var database = await CreateDbAsync(admin, key, new { name = "Contacts", columns = new object[] { new { name = "Name", type = "Text" } } });
        var id = database.GetProperty("id").GetGuid();
        var col = Ids(database)[0];
        var row = await AddRowAsync(admin, id, new Dictionary<string, object> { [col] = "Ada" });

        var (current, etag) = await GetWithETagAsync(admin, $"api/v1/databases/{id}");
        var renamed = new { name = "People", columns = new object[] { new { id = col, name = "Full name", type = "Text" }, new { name = "Born", type = "Date" } } };
        using (var missing = await admin.SendAsync(HttpMethod.Put, $"api/v1/databases/{id}", renamed, withXsrf: true))
        {
            await Browser.AssertStatusAsync(missing, HttpStatusCode.PreconditionRequired);
        }

        using (var ok = await admin.SendAsync(HttpMethod.Put, $"api/v1/databases/{id}", renamed, withXsrf: true, ifMatch: etag))
        {
            var updated = await ReadAsync(ok);
            Assert.Equal(col, updated.GetProperty("columns")[0].GetProperty("id").GetString()); // id survives the rename
            Assert.Equal("Full name", updated.GetProperty("columns")[0].GetProperty("name").GetString());
        }

        using (var stale = await admin.SendAsync(HttpMethod.Put, $"api/v1/databases/{id}", renamed, withXsrf: true, ifMatch: etag))
        {
            await Browser.AssertStatusAsync(stale, HttpStatusCode.PreconditionFailed);
        }

        using (var locked = await admin.SendAsync(HttpMethod.Put, $"api/v1/databases/{id}", new { name = "People", columns = new object[] { new { id = col, name = "Full name", type = "Number" } } }, withXsrf: true, ifMatch: "\"r2\""))
        {
            await Browser.AssertStatusAsync(locked, HttpStatusCode.BadRequest, "db.column_type_locked");
        }

        // The existing row survives the schema change and can be edited with its own ETag.
        Assert.Equal("Ada", (await NamesAsync(admin, $"api/v1/databases/{id}/rows", col))[0]);
        var (r, rowTag) = await GetWithETagAsync(admin, $"api/v1/database-rows/{Id(row)}");
        using (var edit = await admin.SendAsync(HttpMethod.Put, $"api/v1/database-rows/{Id(r)}", new { values = new Dictionary<string, object> { [col] = "Ada L." } }, withXsrf: true, ifMatch: rowTag))
        {
            Assert.Equal("Ada L.", (await ReadAsync(edit)).GetProperty("values").GetProperty(col).GetString());
        }

        using (var staleRow = await admin.SendAsync(HttpMethod.Put, $"api/v1/database-rows/{Id(r)}", new { values = new Dictionary<string, object>() }, withXsrf: true, ifMatch: rowTag))
        {
            await Browser.AssertStatusAsync(staleRow, HttpStatusCode.PreconditionFailed);
        }

        using (var delRow = await admin.DeleteAsync($"api/v1/database-rows/{Id(r)}"))
        {
            await Browser.AssertStatusAsync(delRow, HttpStatusCode.NoContent);
        }

        await AddRowAsync(admin, id, new Dictionary<string, object> { [col] = "Grace" });
        using (var del = await admin.DeleteAsync($"api/v1/databases/{id}"))
        {
            await Browser.AssertStatusAsync(del, HttpStatusCode.NoContent);
        }

        using (var gone = await admin.GetAsync($"api/v1/databases/{id}/rows"))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }

        _ = current;
    }

    [Fact]
    public async Task Permissions_follow_the_space_and_other_tenants_see_nothing()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (viewer, _, viewerId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var (outsider, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var adminId = await MeAsync(admin);

        var key = (await CreateSpaceAsync(admin, visibility: "Private")).GetProperty("key").GetString()!;
        var grants = new[] { "View", "ManagePermissions", "Edit", "Create", "Publish", "Move", "Archive", "Delete", "Comment", "ManageTemplates" }
            .Select(a => (object)new { subjectType = "User", subjectId = (Guid?)adminId, action = a, effect = "Allow" })
            .Append(new { subjectType = "User", subjectId = (Guid?)viewerId, action = "View", effect = "Allow" }).ToList();
        using (var put = await admin.PutAsync($"api/v1/spaces/{key}/permissions", new { grants }))
        {
            await ReadAsync(put);
        }

        var database = await CreateDbAsync(admin, key, new { name = "Secret", columns = Schema });
        var id = database.GetProperty("id").GetGuid();
        var row = await AddRowAsync(admin, id, new Dictionary<string, object> { [Ids(database)[0]] = "classified" });

        Assert.False((await viewer.GetJsonAsync($"api/v1/databases/{id}")).GetProperty("canEdit").GetBoolean());
        Assert.Equal(1, (await viewer.GetJsonAsync($"api/v1/spaces/{key}/databases")).GetArrayLength());
        Assert.Equal(1, (await viewer.GetJsonAsync($"api/v1/databases/{id}/rows")).GetProperty("total").GetInt32());

        using (var create = await viewer.PostAsync($"api/v1/spaces/{key}/databases", new { name = "nope" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        }

        using (var add = await viewer.PostAsync($"api/v1/databases/{id}/rows", new { values = new Dictionary<string, object>() }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, add.StatusCode);
        }

        using (var del = await viewer.DeleteAsync($"api/v1/database-rows/{Id(row)}"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, del.StatusCode);
        }

        // Same-tenant members without access, and other tenants, learn nothing.
        var (_, otherAdmin, _) = await _kit.Identity.TenantWithAdminAsync();
        foreach (var who in new[] { outsider, otherAdmin })
        {
            foreach (var url in new[] { $"api/v1/databases/{id}", $"api/v1/databases/{id}/rows", $"api/v1/database-rows/{Id(row)}", $"api/v1/databases/{id}/export" })
            {
                using var r = await who.GetAsync(url);
                Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
            }
        }

        using (var hiddenList = await outsider.GetAsync($"api/v1/spaces/{key}/databases"))
        {
            Assert.Equal(HttpStatusCode.NotFound, hiddenList.StatusCode);
        }
    }
}

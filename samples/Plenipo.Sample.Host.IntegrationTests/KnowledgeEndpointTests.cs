using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// The curator surface over HTTP, on the real host. Deliberately endpoint-level rather than
/// service-level: the service was already covered, and the first bug this surface shipped was an
/// EF projection that only failed once a request actually ran it.
/// </summary>
[Collection("api")]
public sealed class KnowledgeEndpointTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Create_index_inspect_and_search_a_curated_collection()
    {
        using var client = fixture.ClientFor("system_admin");

        // The language picker is server-driven, so an unknown configuration can never be offered.
        var languages = (await client.GetFromJsonAsync<List<string>>("/api/knowledge/languages"))!;
        Assert.Contains("spanish", languages);
        Assert.Contains("simple", languages);

        var create = await client.PostAsJsonAsync("/api/knowledge", new
        {
            name = "endpoint: ES statutes",
            language = "spanish",
            metadata = new Dictionary<string, string> { ["jurisdiction"] = "ES" },
        });
        create.EnsureSuccessStatusCode();
        var collectionId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var fileId = await UploadAsync(client, "estatuto.txt",
            "El trabajador tendra derecho a una indemnizacion por despido improcedente calculada segun los " +
            "anos de servicio prestados, y el preaviso minimo sera de quince dias naturales.");

        var index = await client.PostAsJsonAsync($"/api/knowledge/{collectionId}/documents", new
        {
            fileIds = new[] { fileId },
            metadata = new Dictionary<string, string> { ["jurisdiction"] = "ES", ["effectiveYear"] = "2026" },
        });
        Assert.Equal(HttpStatusCode.Accepted, index.StatusCode);
        var jobId = (await index.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetGuid();
        await WaitForJobAsync(client, jobId);

        // The listing reports size and the facet keys discovered from the corpus.
        var collections = await client.GetFromJsonAsync<List<JsonElement>>("/api/knowledge");
        var mine = collections!.Single(c => c.GetProperty("id").GetGuid() == collectionId);
        Assert.Equal("spanish", mine.GetProperty("language").GetString());
        Assert.Equal(1, mine.GetProperty("documentCount").GetInt32());
        Assert.True(mine.GetProperty("chunkCount").GetInt32() > 0);
        Assert.True(mine.GetProperty("isEditable").GetBoolean());
        var filterKeys = mine.GetProperty("filterKeys").EnumerateArray().Select(k => k.GetString()).ToList();
        Assert.Contains("jurisdiction", filterKeys);
        Assert.Contains("effectiveYear", filterKeys);

        // The documents projection: a GroupBy that must actually translate.
        var documents = await client.GetFromJsonAsync<List<JsonElement>>($"/api/knowledge/{collectionId}/documents");
        var document = Assert.Single(documents!);
        Assert.Equal("estatuto.txt", document.GetProperty("fileName").GetString());
        Assert.Equal("spanish", document.GetProperty("language").GetString());
        Assert.True(document.GetProperty("chunkCount").GetInt32() > 0);

        // Spanish stemming end to end: a plural query reaches the singular in the source text.
        var hits = await SearchAsync(client, new
        {
            query = "indemnizaciones por despido improcedente",
            filters = new Dictionary<string, string> { ["jurisdiction"] = "ES" },
        });
        Assert.Contains(hits, h => h.GetProperty("fileName").GetString() == "estatuto.txt");

        // A facet that matches nothing returns nothing, rather than falling back to everything.
        Assert.Empty(await SearchAsync(client, new
        {
            query = "indemnizaciones por despido improcedente",
            filters = new Dictionary<string, string> { ["jurisdiction"] = "JP" },
        }));

        // Cleanup keeps the shared dev tenant tidy for the other tests in this collection.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/knowledge/{collectionId}")).StatusCode);
    }

    [Fact]
    public async Task Curating_requires_the_knowledge_permission()
    {
        // "user" holds the retrieval tools but not platform.knowledge.manage: it may look, not build.
        using var user = fixture.ClientFor("user");

        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/knowledge")).StatusCode);

        var create = await user.PostAsJsonAsync("/api/knowledge", new { name = "should not exist" });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    [Fact]
    public async Task A_module_owned_collection_is_not_editable_from_the_curator_surface()
    {
        using var admin = fixture.ClientFor("system_admin");

        var collections = await admin.GetFromJsonAsync<List<JsonElement>>("/api/knowledge");
        var bound = collections!.FirstOrDefault(c => c.TryGetProperty("resourceType", out var rt) && rt.ValueKind is JsonValueKind.String);
        if (bound.ValueKind is JsonValueKind.Undefined)
        {
            return; // no matter has been indexed in this run — nothing to assert against
        }

        var id = bound.GetProperty("id").GetGuid();
        Assert.False(bound.GetProperty("isEditable").GetBoolean());

        // A matter's corpus belongs to its matter; deleting it from a generic admin screen would be
        // a surprise, so the endpoint refuses rather than silently succeeding.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/knowledge/{id}")).StatusCode);
    }

    // --- helpers ---------------------------------------------------------------------------------

    private static async Task<Guid> UploadAsync(HttpClient client, string fileName, string content)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", fileName);

        var response = await client.PostAsync("/api/files", form);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<List<JsonElement>> SearchAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/api/knowledge/search", body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<JsonElement>>())!;
    }

    private static async Task WaitForJobAsync(HttpClient client, Guid jobId)
    {
        for (var i = 0; i < 120; i++)
        {
            var job = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}");
            var status = job.GetProperty("status").GetString();
            if (status is "Succeeded")
            {
                return;
            }

            Assert.NotEqual("Failed", status);
            await Task.Delay(250);
        }

        Assert.Fail($"ingest job {jobId} did not finish in time");
    }
}

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Thalovant.Sdk.Tests;

/// <summary>
/// What a control-plane error carries, against the vectors every SDK shares:
/// contracts/conformance/api-error-vectors.json in the Python SDK, vendored
/// here and pinned by the parity contract.
/// </summary>
/// <remarks>
/// The API answers a refusal with a Problem+JSON body whose structured fields
/// say what to do next -- the images a caller may pin instead, the plan's
/// numbers -- and a message cut at 200 characters is not where anybody can
/// read them. Each case's status, Content-Type and body bytes are served by
/// the handler the control plane sends through, and read back through
/// <see cref="ThalovantControlPlane.GetHubAsync"/>, so what is recorded is
/// what a caller gets.
/// </remarks>
public sealed class ApiErrorVectorTests {
    private static JsonNode Vectors(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)))!;

    private static JsonArray Cases => Vectors("api-error-vectors.json")["cases"]!.AsArray();

    public static TheoryData<string> CaseNames {
        get {
            var names = new TheoryData<string>();
            foreach (var item in Cases) names.Add((string)item!["name"]!);
            return names;
        }
    }

    private static JsonNode Case(string name) => Cases.Single(item => (string)item!["name"]! == name)!;

    /// <summary>Answers every request with one response, byte for byte.</summary>
    private sealed class Answering : HttpMessageHandler {
        private readonly int _status;
        private readonly string _contentType;
        private readonly byte[] _body;

        internal Answering(int status, string contentType, byte[] body) {
            _status = status;
            _contentType = contentType;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            var content = new ByteArrayContent(_body);
            // The header exactly as given. StringContent would add a charset,
            // and the API sends application/problem+json without one.
            content.Headers.TryAddWithoutValidation("Content-Type", _contentType);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)_status) {
                Content = content,
                RequestMessage = request,
            });
        }
    }

    private static async Task<ThalovantApiException> Refusal(int status, string contentType, byte[] body) {
        using var handler = new Answering(status, contentType, body);
        var api = new ThalovantControlPlane(handler, apiUrl: "https://api.example.test", accessToken: "synthetic-token");
        // Any kind: a plan limit, a 401 and a 409 each arrive as their own
        // subclass, and every one of them carries these fields.
        return await Assert.ThrowsAnyAsync<ThalovantApiException>(() => api.GetHubAsync("hub-1"));
    }

    private static Task<ThalovantApiException> Refusal(JsonNode response) => Refusal(
        response["status"]!.GetValue<int>(),
        (string)response["content_type"]!,
        Encoding.UTF8.GetBytes((string)response["body"]!));

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task AnApiErrorCarriesWhatItsVectorNames(string name) {
        var vector = Case(name);
        var error = await Refusal(vector["response"]!);
        var produced = new JsonObject {
            ["status"] = error.StatusCode,
            ["code"] = error.ErrorCode,
            ["detail"] = error.Detail,
            ["problem"] = error.Problem,
        };
        // Recorded before the assert: the record is what this SDK produced,
        // not a restatement of what the vector says it should have.
        ConformanceRecord.Record("api-error-vectors.json", name, produced.DeepClone());
        var expect = vector["expect"]!;
        Assert.True(JsonNode.DeepEquals(expect, produced), $"{name}: produced {produced.ToJsonString()}");
        Assert.Equal(ConformanceRecord.CanonicalDigest(expect), ConformanceRecord.CanonicalDigest(produced));
        // The sentence character for character: no trimming, no collapsing.
        Assert.Equal((string?)expect["detail"], error.Detail);
        foreach (var item in vector["message_excludes"]?.AsArray() ?? new JsonArray()) {
            var echoed = (string)item!;
            Assert.DoesNotContain(echoed, error.Message, StringComparison.Ordinal);
            // What a logger prints for an exception.
            Assert.DoesNotContain(echoed, error.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheMessageMayBeShortenedButTheDetailNeverIs() {
        var vector = Cases.First(item => (string?)item!["expect"]!["code"] == "platform_image_required")!;
        var error = await Refusal(vector["response"]!);
        var detail = (string)vector["expect"]!["detail"]!;

        // The display line is what it always was: the status and as much of the
        // sentence as fits in MaxServerDetailLength characters.
        const string prefix = "Thalovant API request failed with HTTP 403: ";
        Assert.StartsWith(prefix + "Only an administrator", error.Message, StringComparison.Ordinal);
        var shown = error.Message.Substring(prefix.Length);
        Assert.True(shown.Length <= ThalovantControlPlane.MaxServerDetailLength);
        Assert.StartsWith(shown, detail, StringComparison.Ordinal);
        Assert.True(shown.Length < detail.Length, "the image refusal is expected to be longer than the message keeps");

        // The sentence the API wrote is whole, and every list it sent is there.
        Assert.Equal(detail, error.Detail);
        Assert.True(error.Detail!.Length > ThalovantControlPlane.MaxServerDetailLength);
        Assert.Equal("platform_image_required", error.ErrorCode);
        var problem = error.Problem!;
        Assert.Equal(
            new[] { "ghcr.io/thalovant/ovos-core:2026.09.2", "ghcr.io/thalovant/ovos-core:2026.09.3-alpha.1" },
            problem["allowed_images"]!["core"]!.AsArray().Select(image => (string)image!));
        Assert.Equal(3, problem["allowed_images"]!["bus"]!.AsArray().Count);
        Assert.Equal("ghcr.io/thalovant/ovos-core", (string?)problem["allowed_repositories"]!["core"]);
        Assert.Equal("docker.io/example/ovos-messagebus:custom", (string?)problem["refused_images"]!["bus"]);
        Assert.Equal("docker.io/example/ovos-core:custom", (string?)problem["refused_images"]!["core"]);
    }

    [Fact]
    public async Task APlanLimitKeepsItsNumbers() {
        var vector = Cases.First(item =>
            (string?)item!["expect"]!["code"] == "plan_limit" && item["expect"]!["problem"]!["resource"] is not null)!;
        var error = await Refusal(vector["response"]!);
        var problem = error.Problem!;
        Assert.Equal("plan_limit", error.ErrorCode);
        Assert.Equal("client", (string?)problem["resource"]);
        Assert.Equal(1, problem["limit"]!.GetValue<int>());
        Assert.Equal(1, problem["used"]!.GetValue<int>());
        Assert.Equal("Free", (string?)problem["plan"]);
    }

    [Fact]
    public void AnErrorBuiltTheOldWayStillReadsTheOldWay() {
        var local = new ThalovantApiException("Missing Thalovant API access token.");
        Assert.Equal("Missing Thalovant API access token.", local.Message);
        Assert.Null(local.StatusCode);
        Assert.Null(local.Body);
        Assert.Null(local.ErrorCode);
        Assert.Null(local.Detail);
        Assert.Null(local.Problem);

        var conflict = new ThalovantApiException("conflict", 412);
        Assert.Equal(412, conflict.StatusCode);
        Assert.Null(conflict.ErrorCode);
        Assert.Null(conflict.Detail);
        Assert.Null(conflict.Problem);

        // A body passed the old way now also gives its detail and problem, and
        // an error code passed explicitly still wins over the body's.
        const string plan = """{"detail": "Free plan allows up to 1 connection.", "code": "plan_limit", "limit": 1}""";
        var explicitCode = new ThalovantApiException("refused", 403, plan, "other");
        Assert.Equal("other", explicitCode.ErrorCode);
        Assert.Equal("Free plan allows up to 1 connection.", explicitCode.Detail);
        Assert.Equal("plan_limit", (string?)explicitCode.Problem!["code"]);
        Assert.Equal(plan, explicitCode.Body);
        Assert.Equal("refused", explicitCode.Message);

        var named = new ThalovantApiException("HTTP 401", statusCode: 401, body: """{"detail": {"code": "mfa_required"}}""");
        Assert.Equal("mfa_required", named.ErrorCode);
        Assert.Null(named.Detail);

        // The same public constructor, with the same binary signature, and no other.
        var constructors = typeof(ThalovantApiException).GetConstructors();
        Assert.Single(constructors);
        Assert.Equal(
            new[] { typeof(string), typeof(int?), typeof(string), typeof(string) },
            constructors[0].GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public void EachReadOfTheProblemIsItsOwnCopy() {
        var error = new ThalovantApiException(
            "refused", 403, """{"detail": "Free plan allows up to 1 connection.", "code": "plan_limit", "limit": 1}""");
        var first = error.Problem!;
        first["limit"] = 99;
        first.Remove("code");
        Assert.NotSame(first, error.Problem);
        Assert.Equal(1, error.Problem!["limit"]!.GetValue<int>());
        Assert.Equal("plan_limit", (string?)error.Problem!["code"]);
        Assert.Equal("plan_limit", error.ErrorCode);
    }

    [Fact]
    public async Task TheBodyIsReadAsUtf8WhateverTheContentTypeSays() {
        var vector = Cases.First(item => ((string?)item!["expect"]!["detail"])?.Contains('\n') == true)!;
        var response = vector["response"]!;
        var bytes = Encoding.UTF8.GetBytes((string)response["body"]!);
        var expect = vector["expect"]!;

        // A charset a proxy put on the response does not re-decode the bytes.
        var labelled = await Refusal(
            response["status"]!.GetValue<int>(), "application/problem+json; charset=iso-8859-1", bytes);
        Assert.Equal((string)expect["detail"]!, labelled.Detail);
        Assert.True(JsonNode.DeepEquals(expect["problem"], labelled.Problem));

        // Nor does a byte-order mark stop the body parsing.
        var marked = await Refusal(
            response["status"]!.GetValue<int>(), "application/problem+json",
            new byte[] { 0xEF, 0xBB, 0xBF }.Concat(bytes).ToArray());
        Assert.Equal((string)expect["detail"]!, marked.Detail);
        Assert.True(JsonNode.DeepEquals(expect["problem"], marked.Problem));
    }

    [Fact]
    public async Task ANameTheBodyRepeatsKeepsItsLastValue() {
        // JsonNode fills an object lazily and throws ArgumentException on a
        // repeated name at first access, which escaped GetHubAsync. Every other
        // SDK's decoder keeps the last value.
        var error = await Refusal(403, "application/json",
            Encoding.UTF8.GetBytes("""{"code": "first", "detail": {"x": 1, "x": 2}, "code": "plan_limit"}"""));
        Assert.Equal("plan_limit", error.ErrorCode);
        Assert.Equal("plan_limit", (string?)error.Problem!["code"]);
        Assert.Equal(2, error.Problem!["detail"]!["x"]!.GetValue<int>());
        Assert.Equal("Thalovant API request failed with HTTP 403: plan_limit", error.Message);
    }

    [Fact]
    public void TheVectorsCoverEveryShapeTheRulesName() {
        // A vector set that quietly lost its non-JSON or its nested case would still pass.
        var expects = Cases.Select(item => item!["expect"]!).ToList();
        Assert.Contains(expects, e => e["problem"] is null);
        Assert.Contains(expects, e => e["code"] is not null && e["detail"] is null);
        Assert.Contains(expects, e => e["detail"] is not null && e["code"] is null);
        Assert.Contains(expects, e => e["problem"]?["detail"] is JsonObject);
        Assert.Contains(expects, e => e["problem"]?["detail"] is JsonArray);
        Assert.Contains(expects, e => ((string?)e["detail"] ?? "").Length > 256);
        Assert.Contains(expects, e => ((string?)e["detail"] ?? "").Contains('\n'));
        Assert.Contains(Cases, item => item!["message_excludes"] is JsonArray { Count: > 0 });
    }
}

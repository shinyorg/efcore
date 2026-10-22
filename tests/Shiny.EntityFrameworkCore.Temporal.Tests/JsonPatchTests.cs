using System.Text.Json;
using Shiny.EntityFrameworkCore.Temporal.Internal;

namespace Shiny.EntityFrameworkCore.Temporal.Tests;

public class JsonPatchTests
{
    public class Doc
    {
        public string? Name { get; set; }
        public int Count { get; set; }
        public Inner? Inner { get; set; }
        public List<int> Tags { get; set; } = [];
    }

    public class Inner
    {
        public string? A { get; set; }
        public string? B { get; set; }
    }

    static JsonElement Value(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Diff_CoversAddReplaceRemoveAndNesting()
    {
        var patch = JsonDiff.CreatePatch<Doc>(
            """{"Name":"a","Count":1,"Inner":{"A":"x","B":"y"},"Old":1,"Nil":null,"Tags":[1]}""",
            """{"Name":null,"Count":2,"Inner":{"A":"x","B":"z"},"New":true,"Nil":5,"Tags":[1,2],"a/b~c":1}""",
            JsonSerializerOptions.Default);

        var ops = patch.Operations.Select(o => (o.Op, o.Path, o.Value?.GetRawText())).ToList();
        Assert.Equal(
            [
                ("replace", "/Name", "null"),
                ("replace", "/Count", "2"),
                ("replace", "/Inner/B", "\"z\""),
                ("add", "/New", "true"),
                ("add", "/Nil", "5"),
                ("replace", "/Tags", "[1,2]"),
                ("add", "/a~1b~0c", "1"),
                ("remove", "/Old", null)
            ],
            ops);
    }

    [Fact]
    public void ApplyTo_RunsEveryOperation()
    {
        var patch = new JsonPatchDocument<Doc>(
        [
            JsonPatchOperation.Replace("/Name", Value("\"b\"")),
            JsonPatchOperation.Add("/Tags/-", Value("3")),
            JsonPatchOperation.Add("/Tags/0", Value("9")),
            JsonPatchOperation.Copy("/Inner/A", "/Inner/B"),
            JsonPatchOperation.Move("/Count", "/Count"),
            JsonPatchOperation.Test("/Name", Value("\"b\"")),
            JsonPatchOperation.Remove("/Inner/A")
        ]);

        var result = patch.ApplyTo(new Doc { Name = "a", Count = 4, Inner = new Inner { A = "x" }, Tags = [1, 2] });
        Assert.Equal("b", result.Name);
        Assert.Equal(4, result.Count);
        Assert.Equal([9, 2, 3], result.Tags);
        Assert.Null(result.Inner!.A);
        Assert.Equal("x", result.Inner.B);
    }

    [Fact]
    public void ApplyTo_FailingTestOrUnknownOp_Throws()
    {
        var failing = new JsonPatchDocument<Doc>([JsonPatchOperation.Test("/Name", Value("\"nope\""))]);
        Assert.Throws<InvalidOperationException>(() => failing.ApplyTo(new Doc { Name = "a" }));

        var root = new JsonPatchDocument<Doc>([JsonPatchOperation.Remove("")]);
        Assert.Throws<InvalidOperationException>(() => root.ApplyTo(new Doc()));

        var missing = new JsonPatchDocument<Doc>([JsonPatchOperation.Replace("/Inner/A", Value("1"))]);
        Assert.Throws<InvalidOperationException>(() => missing.ApplyTo(new Doc()));
    }

    [Fact]
    public void ToJson_IsRfc6902()
    {
        var patch = new JsonPatchDocument<Doc>(
        [
            JsonPatchOperation.Replace("/Name", Value("\"b\"")),
            JsonPatchOperation.Move("/A", "/B"),
            JsonPatchOperation.Remove("/Count")
        ]);
        Assert.Equal(
            """[{"op":"replace","path":"/Name","value":"b"},{"op":"move","from":"/A","path":"/B"},{"op":"remove","path":"/Count"}]""",
            patch.ToJson());
    }
}

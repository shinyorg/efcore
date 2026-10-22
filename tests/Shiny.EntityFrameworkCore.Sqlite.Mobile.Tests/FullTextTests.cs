using Microsoft.EntityFrameworkCore;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile.Tests;

public class FullTextTests
{
    [Fact]
    public void FullTextMatch_searches_every_indexed_column()
    {
        using var db = new TestDatabase();
        using var ctx = Seeded(db);

        Assert.Equal(["Gardening basics", "Tomato season"], Titles(ctx.Docs.Where(d => EF.Functions.FullTextMatch(d, "tomato"))));
        Assert.Equal(["Gardening basics"], Titles(ctx.Docs.Where(d => EF.Functions.FullTextMatch(d, "soil AND tomato"))));
        Assert.Equal(["Gardening basics", "Tomato season", "Weekend baking"], Titles(ctx.Docs.Where(d => EF.Functions.FullTextMatch(d, "tomato OR bread"))));
        Assert.Equal(["Tomato season"], Titles(ctx.Docs.Where(d => EF.Functions.FullTextMatch(d, "tomato NOT soil"))));
        Assert.Equal(["Weekend baking"], Titles(ctx.Docs.Where(d => EF.Functions.FullTextMatch(d, "\"sourdough bread\""))));
        Assert.Equal(["Weekend baking"], Titles(ctx.Docs.Where(d => EF.Functions.FullTextMatch(d, "sourd*"))));
        Assert.Empty(Titles(ctx.Docs.Where(d => EF.Functions.FullTextMatch(d, "spaceship"))));

        // the query string is a normal EF parameter
        var term = "bread";
        var query = ctx.Docs.Where(d => EF.Functions.FullTextMatch(d, term));
        Assert.Equal(["Weekend baking"], Titles(query));
        var sql = query.ToQueryString();
        Assert.Contains("\"d\".\"Id\" IN (", sql);
        Assert.Contains("FROM \"Docs_fts\"", sql);
        Assert.Contains("match(@term,", sql);
        Assert.Contains("SEARCH d USING INTEGER PRIMARY KEY", Geo.Plan(query));
    }

    [Fact]
    public void FullTextMatch_on_a_property_scopes_to_that_column()
    {
        using var db = new TestDatabase();
        using var ctx = Seeded(db);

        Assert.Equal(["Tomato season"], Titles(ctx.Docs.Where(d => EF.Functions.FullTextMatch(d.Title, "tomato"))));
        Assert.Equal(["Gardening basics", "Tomato season"], Titles(ctx.Docs.Where(d => EF.Functions.FullTextMatch(d.Body, "tomato"))));
        Assert.Equal(["Gardening basics"], Titles(ctx.Docs.Where(d => EF.Functions.FullTextMatch(d.Body, "soil OR basics"))));
        Assert.Contains("{\"Title\"} : (", ctx.Docs.Where(d => EF.Functions.FullTextMatch(d.Title, "x")).ToQueryString());

        var notIndexed = Assert.Throws<InvalidOperationException>(() => ctx.Docs.Where(d => EF.Functions.FullTextMatch(d.Tag, "x")).ToList());
        Assert.Contains("not part of the full-text index", notIndexed.Message);

        var notAProperty = Assert.Throws<InvalidOperationException>(() => ctx.Docs.Where(d => EF.Functions.FullTextMatch(d.Title + "!", "x")).ToList());
        Assert.Contains("direct property access", notAProperty.Message);
    }

    [Fact]
    public void FullTextRank_orders_best_match_first()
    {
        using var db = new TestDatabase();
        using var ctx = Seeded(db);

        var ranked = ctx.Docs
            .Where(d => EF.Functions.FullTextMatch(d, "tomato"))
            .OrderBy(d => EF.Functions.FullTextRank(d, "tomato"))
            .Select(d => new { d.Title, Rank = EF.Functions.FullTextRank(d, "tomato") })
            .ToList();

        // "Tomato season" mentions tomato three times in a short document; bm25 ranks it first (lower = better).
        Assert.Equal(["Tomato season", "Gardening basics"], ranked.Select(r => r.Title));
        Assert.True(ranked[0].Rank < ranked[1].Rank);
        Assert.True(ranked[0].Rank < 0);

        // rows that don't match have no rank
        Assert.Null(ctx.Docs.Where(d => d.Title == "Weekend baking").Select(d => EF.Functions.FullTextRank(d, "tomato")).Single());
    }

    [Fact]
    public void FullText_composes_with_filters_ordering_paging_and_async()
    {
        using var db = new TestDatabase();
        using var ctx = db.Mobile();
        for (var i = 0; i < 20; i++)
            ctx.Docs.Add(new Doc { Title = $"note {i:00}", Body = i % 2 == 0 ? "alpha beta" : "gamma", Tag = i % 4 == 0 ? "x" : "y" });
        ctx.SaveChanges();

        var page = ctx.Docs
            .Where(d => d.Tag == "x" && EF.Functions.FullTextMatch(d, "alpha"))
            .OrderByDescending(d => d.Title)
            .Skip(1)
            .Take(2)
            .Select(d => d.Title)
            .ToList();
        Assert.Equal(["note 12", "note 08"], page);

        Assert.Equal(10, ctx.Docs.Count(d => EF.Functions.FullTextMatch(d, "alpha")));
        Assert.True(ctx.Docs.Any(d => EF.Functions.FullTextMatch(d, "gamma") || d.Title == "nope"));
        Assert.Equal(10, ctx.Docs.Count(d => !EF.Functions.FullTextMatch(d, "alpha")));
    }

    [Fact]
    public async Task FullText_async_query()
    {
        using var db = new TestDatabase();
        using var ctx = Seeded(db);
        var titles = await ctx.Docs.Where(d => EF.Functions.FullTextMatch(d, "bread")).Select(d => d.Title).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["Weekend baking"], titles);
    }

    [Fact]
    public void Triggers_keep_the_index_in_sync_for_every_write_path()
    {
        using var db = new TestDatabase();
        using var ctx = db.Mobile();
        var doc = new Doc { Title = "first draft", Body = "lorem" };
        ctx.Docs.Add(doc);
        ctx.SaveChanges();
        Assert.Equal(1, Count(ctx, "draft"));

        doc.Title = "final copy";
        ctx.SaveChanges();
        Assert.Equal(0, Count(ctx, "draft"));
        Assert.Equal(1, Count(ctx, "final"));

        doc.Body = null;
        ctx.SaveChanges();
        Assert.Equal(0, Count(ctx, "lorem"));

        ctx.Docs.Where(d => d.Id == doc.Id).ExecuteUpdate(s => s.SetProperty(d => d.Body, "ipsum"));
        Assert.Equal(1, Count(ctx, "ipsum"));

        // updating a column that isn't indexed doesn't disturb the index
        ctx.Docs.Where(d => d.Id == doc.Id).ExecuteUpdate(s => s.SetProperty(d => d.Tag, "t"));
        Assert.Equal(1, Count(ctx, "ipsum"));

        ctx.Database.ExecuteSql($"INSERT INTO \"Docs\" (\"Title\", \"Body\") VALUES ('raw sql row', 'zeta')");
        Assert.Equal(1, Count(ctx, "zeta"));

        ctx.Docs.Where(d => d.Title == "raw sql row").ExecuteDelete();
        Assert.Equal(0, Count(ctx, "zeta"));

        ctx.ChangeTracker.Clear();
        ctx.Docs.Remove(ctx.Docs.Single());
        ctx.SaveChanges();
        Assert.Equal(0, Count(ctx, "final"));

        // integrity-check: the external-content index agrees with its content table
        ctx.Database.ExecuteSqlRaw("INSERT INTO \"Docs_fts\" (\"Docs_fts\") VALUES ('integrity-check')");
    }

    [Fact]
    public void Full_text_only_context_with_tokenizer()
    {
        using var db = new TestDatabase();
        using var ctx = db.Create(o => new FullTextOnlyContext(o), o => o.UseShinyFullText());
        ctx.Articles.Add(new Article { Title = "Running shoes", Content = "Runners love them", Scores = [1, 2] });
        ctx.Articles.Add(new Article { Title = "Swimming", Content = "Pools" });
        ctx.SaveChanges();

        // porter stemming: "run" matches "Running" / "Runners"
        Assert.Equal(["Running shoes"], ctx.Articles.Where(a => EF.Functions.FullTextMatch(a, "run")).Select(a => a.Title).ToList());
        Assert.Contains("tokenize='porter unicode61'", Geo.Strings(ctx, "SELECT sql FROM sqlite_master WHERE name = 'Articles_fts'").Single());

        // vectors are off: float[] keeps EF's JSON mapping
        Assert.Equal(["[1,2]", ""], Geo.Strings(ctx, "SELECT \"Scores\" FROM \"Articles\" ORDER BY \"Id\""));
    }

    [Fact]
    public void FullTextQuery_Escape_makes_user_input_safe()
    {
        Assert.Equal("\"c++\" \"AND\" \"don't\"", FullTextQuery.Escape("c++ AND don't"));
        Assert.Equal("\"say\" \"\"\"hi\"\"\"", FullTextQuery.Escape("say \"hi\""));
        Assert.Equal("\"sour\" \"dou\"*", FullTextQuery.Escape("  sour   dou ", prefixLastTerm: true));
        Assert.Equal("\"\"", FullTextQuery.Escape("   "));
        Assert.Equal("\"\"", FullTextQuery.Escape(null));

        using var db = new TestDatabase();
        using var ctx = Seeded(db);
        ctx.Docs.Add(new Doc { Title = "C++ tips", Body = "pointers: don't panic" });
        ctx.SaveChanges();

        var raw = "don't: panic";
        Assert.ThrowsAny<Exception>(() => ctx.Docs.Where(d => EF.Functions.FullTextMatch(d, raw)).ToList());

        var safe = FullTextQuery.Escape(raw);
        Assert.Equal(["C++ tips"], Titles(ctx.Docs.Where(d => EF.Functions.FullTextMatch(d, safe))));
        var prefix = FullTextQuery.Escape("poin", prefixLastTerm: true);
        Assert.Equal(["C++ tips"], Titles(ctx.Docs.Where(d => EF.Functions.FullTextMatch(d, prefix))));
        var empty = FullTextQuery.Escape("");
        Assert.Empty(Titles(ctx.Docs.Where(d => EF.Functions.FullTextMatch(d, empty))));
    }

    [Fact]
    public void Guards()
    {
        using var db = new TestDatabase();
        using var ctx = db.Mobile();

        var noIndex = Assert.Throws<InvalidOperationException>(() => ctx.Notes.Where(n => EF.Functions.FullTextMatch(n, "x")).ToList());
        Assert.Contains("has no full-text index", noIndex.Message);
        Assert.Throws<InvalidOperationException>(() => ctx.Notes.OrderBy(n => EF.Functions.FullTextRank(n, "x")).ToList());

        // Only meaningful in a query.
        Assert.Throws<InvalidOperationException>(() => EF.Functions.FullTextMatch(new Doc(), "x"));
        Assert.Throws<InvalidOperationException>(() => EF.Functions.FullTextMatch("text", "x"));
        Assert.Throws<InvalidOperationException>(() => EF.Functions.FullTextRank(new Doc(), "x"));
    }

    static MobileContext Seeded(TestDatabase db)
    {
        var ctx = db.Mobile();
        ctx.Docs.Add(new Doc { Title = "Gardening basics", Body = "Good soil grows a good tomato." });
        ctx.Docs.Add(new Doc { Title = "Tomato season", Body = "Tomato, tomato, tomato." });
        ctx.Docs.Add(new Doc { Title = "Weekend baking", Body = "A sourdough bread recipe." });
        ctx.SaveChanges();
        ctx.ChangeTracker.Clear();
        return ctx;
    }

    static List<string> Titles(IQueryable<Doc> query) => query.Select(d => d.Title).OrderBy(t => t).ToList();

    static long Count(MobileContext ctx, string term) => ctx.Docs.Count(d => EF.Functions.FullTextMatch(d, term));
}

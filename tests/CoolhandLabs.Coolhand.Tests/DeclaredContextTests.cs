using CoolhandLabs.Internal;

namespace CoolhandLabs.Tests;

public class DeclaredContextTests
{
    private static readonly DeclaredContext Outer = new DeclaredContext { Reporter = "outer" }
        .WithWorkload("qa/plans", "QA plans", "Writes plans").WithTemplate("qa/plans/generate", "generate");

    [Fact]
    public void IsActiveOnlyInsideTheBlock()
    {
        Assert.Null(Coolhand.CurrentContext);
        var seen = Coolhand.WithContext(Outer, () => Coolhand.CurrentContext);
        Assert.Equal("outer", seen!.Reporter);
        Assert.Null(Coolhand.CurrentContext);
    }

    [Fact]
    public void InnermostFieldsWinAndAWorkloadResetsTheTemplate()
    {
        Coolhand.WithContext(Outer, () =>
        {
            var reporterOnly = Coolhand.WithContext(new DeclaredContext { Reporter = "inner" }, () => Coolhand.CurrentContext)!;
            Assert.Equal("inner", reporterOnly.Reporter);
            Assert.Equal("qa/plans", reporterOnly.Workload!.Key);
            Assert.Equal("qa/plans/generate", reporterOnly.Template!.Key);

            var newWorkload = Coolhand.WithContext(new DeclaredContext().WithWorkload("other"), () => Coolhand.CurrentContext)!;
            Assert.Equal("outer", newWorkload.Reporter);
            Assert.Equal("other", newWorkload.Workload!.Key);
            Assert.Null(newWorkload.Template);

            var blankReporter = Coolhand.WithContext(new DeclaredContext { Reporter = "  " }, () => Coolhand.CurrentContext)!;
            Assert.Equal("outer", blankReporter.Reporter);
        });
    }

    [Fact]
    public async Task FlowsAcrossAwaitsAndIntoTasks()
    {
        var seen = await Coolhand.WithContextAsync(Outer, async () =>
        {
            await Task.Yield();
            await Task.Delay(1);
            return await Task.Run(() => Coolhand.CurrentContext);
        });
        Assert.Equal("qa/plans", seen!.Workload!.Key);
        Assert.Null(Coolhand.CurrentContext);

        // The synchronous form returns the task; the context still reaches its continuations.
        var viaSync = await Coolhand.WithContext(Outer, async () =>
        {
            await Task.Yield();
            return Coolhand.CurrentContext;
        });
        Assert.Equal("outer", viaSync!.Reporter);
    }

    [Fact]
    public async Task ConcurrentFlowsDoNotSeeEachOther()
    {
        var a = Coolhand.WithContextAsync(new DeclaredContext { Reporter = "a" }, async () =>
        {
            await Task.Delay(20);
            return Coolhand.CurrentContext!.Reporter;
        });
        var b = Coolhand.WithContextAsync(new DeclaredContext { Reporter = "b" }, async () =>
        {
            await Task.Delay(5);
            return Coolhand.CurrentContext!.Reporter;
        });
        Assert.Equal(new[] { "a", "b" }, await Task.WhenAll(a, b));
    }

    [Fact]
    public void ExceptionsPropagateAndTheContextIsRestored()
    {
        Assert.Throws<InvalidOperationException>(() => Coolhand.WithContext<int>(Outer, () => throw new InvalidOperationException()));
        Assert.Null(Coolhand.CurrentContext);
    }

    [Fact]
    public void AnEmptyContextCountsAsNone() => Assert.Null(Coolhand.WithContext(new DeclaredContext(), () => Coolhand.CurrentContext));

    [Theory]
    [InlineData("/bad", "withContext: workload.key must not start or end with \"/\"")]
    [InlineData("a//b", "withContext: workload.key must not contain \"//\"")]
    [InlineData(" x", "withContext: workload.key must not have leading or trailing whitespace")]
    public void InvalidKeysThrowBeforeTheBlockRuns(string key, string message)
    {
        var ran = false;
        var e = Assert.Throws<ArgumentException>(() => Coolhand.WithContext(new DeclaredContext().WithWorkload(key), () => ran = true));
        Assert.Equal(message, e.Message);
        Assert.False(ran);
    }

    [Fact]
    public void OtherMalformedDeclarationsThrow()
    {
        Assert.Throws<ArgumentException>(() => Coolhand.WithContext(new DeclaredContext().WithTemplate("t"), () => 1));
        Assert.Throws<ArgumentException>(() => Coolhand.WithContext(new DeclaredContext { Workload = new DeclaredWorkload(null) }, () => 1));
        Assert.Throws<ArgumentException>(() => Coolhand.WithContext(new DeclaredContext { Template = DeclaredTemplate.OfId(" ") }, () => 1));
        Assert.Throws<ArgumentException>(() =>
            Coolhand.WithContext(new DeclaredContext().WithWorkload("w", null, new string('d', 1001)), () => 1));
        Assert.Throws<ArgumentException>(() => Coolhand.WithContext<int>(null!, () => 1));
        Assert.Throws<ArgumentException>(() => Coolhand.WithContext<int>(Outer, null!));
    }

    [Fact]
    public void ATemplateByIdNeedsNoWorkload()
    {
        var ctx = Coolhand.WithContext(new DeclaredContext { Template = DeclaredTemplate.OfId("tpl_1") }, () => Coolhand.CurrentContext)!;
        Assert.Equal("{\"template\":{\"id\":\"tpl_1\"}}", Json.Stringify(ctx.ToJson()));
    }

    [Fact]
    public void SerializesInNodeFieldOrder() =>
        Assert.Equal("{\"reporter\":\"outer\",\"workload\":{\"key\":\"qa/plans\",\"name\":\"QA plans\",\"description\":\"Writes plans\"},"
                     + "\"template\":{\"key\":\"qa/plans/generate\",\"name\":\"generate\"}}",
            Json.Stringify(Outer.ToJson()));

    [Fact]
    public void WorkloadsById() =>
        Assert.Equal("{\"workload\":{\"id\":\"wkl_1\"}}", Json.Stringify(new DeclaredContext { Workload = DeclaredWorkload.OfId("wkl_1") }.ToJson()));
}

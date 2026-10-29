using ToyCity.Protocol.V0;
using ToyCity.Server;

namespace ToyCity.ClientModel.Tests;

/// <summary>Accounts kept by the server, and who may make one.</summary>
public class AccountTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "toycity-accounts-" + Guid.NewGuid().ToString("N"));
    string File => Path.Combine(_dir, "accounts.json");

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void AnAccountSignsInWithItsPasswordAndNotAnother()
    {
        var store = new AccountStore(File);
        Assert.True(store.Create("Zac", "secret1").Ok);
        Assert.True(store.SignIn("zac", "secret1").Ok);
        Assert.Equal("Zac", store.SignIn("ZAC", "secret1").Name);
        Assert.False(store.SignIn("Zac", "secret2").Ok);
        Assert.False(store.SignIn("Nobody", "secret1").Ok);
        Assert.DoesNotContain("secret1", System.IO.File.ReadAllText(File));
    }

    [Fact]
    public void NamesAreUniqueIgnoringCaseAndFollowTheRules()
    {
        var store = new AccountStore(File);
        Assert.True(store.Create("Mayor Mo", "123456").Ok);
        Assert.False(store.Create("mayor mo", "123456").Ok);
        Assert.False(store.Create("x", "123456").Ok);
        Assert.False(store.Create(" padded", "123456").Ok);
        Assert.False(store.Create("bad/name", "123456").Ok);
        Assert.False(store.Create("Shorty", "12345").Ok);
    }

    [Fact]
    public void ATokenWorksOnceAndSurvivesARestart()
    {
        var store = new AccountStore(File);
        var first = store.Create("Zac", "secret1");
        var again = new AccountStore(File).SignInWithToken(first.Token!);
        Assert.Equal("Zac", again.Name);
        var reloaded = new AccountStore(File);
        Assert.False(reloaded.SignInWithToken(first.Token!).Ok);
        Assert.True(reloaded.SignInWithToken(again.Token!).Ok);
    }

    [Fact]
    public void TokensExpireAfterThirtyDays()
    {
        var now = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);
        var store = new AccountStore(File, () => now);
        var t = store.Create("Zac", "secret1").Token!;
        now = now.AddDays(31);
        Assert.False(store.SignInWithToken(t).Ok);
    }

    [Fact]
    public void TheHostDecidesWhoMayJoin()
    {
        var invite = new HostConfig { Join = JoinPolicy.Invite, InviteCode = "toast" };
        Assert.NotNull(invite.MayCreate("Zac", ""));
        Assert.NotNull(invite.MayCreate("Zac", "jam"));
        Assert.Null(invite.MayCreate("Zac", "toast"));
        Assert.NotNull(new HostConfig { Join = JoinPolicy.Invite }.MayCreate("Zac", ""));
        var list = new HostConfig { Join = JoinPolicy.List, Allowed = { "Zac" } };
        Assert.Null(list.MayCreate("zac", ""));
        Assert.NotNull(list.MayCreate("Mo", ""));
        Assert.Null(new HostConfig().MayCreate("Anyone", ""));
    }

    [Fact]
    public void HostJsonIsWrittenWithTheDefaultsAndTheFirstAccountBecomesAdmin()
    {
        string path = Path.Combine(_dir, "host.json");
        var c = HostConfig.LoadOrCreate(path);
        Assert.True(c.Dedicated);
        Assert.True(System.IO.File.Exists(path));
        c.PromoteFirst("Zac");
        c.PromoteFirst("Mo");
        var again = HostConfig.LoadOrCreate(path);
        Assert.Equal(new[] { "Zac" }, again.Admins);
        Assert.True(again.IsAdmin("zac"));
    }
}

/// <summary>A dedicated server over the wire: nothing but the welcome until a player signs in.</summary>
public class DedicatedServerTests : IClassFixture<DedicatedServerTests.Fixture>
{
    public sealed class Fixture : IDisposable
    {
        public readonly ServerProcess Server = new("""{ "name": "Test town", "join": "invite", "inviteCode": "toast" }""");
        public void Dispose() => Server.Dispose();
    }

    readonly ServerProcess _server;
    public DedicatedServerTests(Fixture f) => _server = f.Server;

    static void WaitFor(CityClient c, Func<bool> done, string what, double seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!done())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"waiting for {what}");
            c.Poll();
            Thread.Sleep(10);
        }
    }

    /// <summary>Signs in as a player, making the account if this server hasn't got it yet (tests share one server).</summary>
    CityClient Join(string name, string password)
    {
        var c = new CityClient(_server.Ws, "test");
        WaitFor(c, () => c.Connection.SignInNeeded, "the server to ask for a sign-in");
        c.Connection.SignIn(name, password, create: true, inviteCode: "toast");
        WaitFor(c, () => c.Connection.Player.Length > 0 || c.Connection.SignInNeeded, "an answer");
        if (c.Connection.SignInNeeded)
        {
            c.Connection.SignIn(name, password);
            WaitFor(c, () => c.Connection.Player.Length > 0, "the sign-in");
        }
        WaitFor(c, () => c.City.Loaded, "the city");
        return c;
    }

    [Fact]
    public void ACityArrivesOnlyAfterSigningIn()
    {
        using var c = new CityClient(_server.Ws, "test");
        var errors = new List<string>();
        c.Message += m => { if (m.BodyCase == ServerMessage.BodyOneofCase.Error) errors.Add(m.Error.Message); };
        WaitFor(c, () => c.Connection.SignInNeeded, "the server to ask for a sign-in");
        Assert.Equal("Test town", c.Connection.Welcome!.ServerName);
        Assert.True(c.Connection.Welcome.InviteRequired);
        c.Connection.Send(new ClientMessage { Why = new WhyRequest { X = 1, Y = 1 } });
        WaitFor(c, () => errors.Count > 0, "the refusal");
        Assert.Equal("Sign in first.", errors[0]);
        Assert.False(c.City.Loaded);

        c.Connection.SignIn("Gatecrasher", "secret1", create: true, inviteCode: "jam");
        WaitFor(c, () => c.Connection.SignInNeeded, "the wrong invite code to be refused");
        Assert.Contains("invite", c.Connection.SignInError);
        Assert.False(c.City.Loaded);

        using var first = Join("Firsty", "secret1");
        Assert.Equal("Firsty", first.Connection.Player);
        Assert.True(first.Connection.Admin);  // the first account on a server with no admins
    }

    [Fact]
    public void OnlyAdminsRunTheServerAndATokenSignsBackIn()
    {
        Join("Firsty", "secret1").Dispose();  // so the next player isn't the first, and isn't promoted
        SignIn token;
        using (var c = Join("Second", "secret2"))
        {
            var errors = new List<string>();
            c.Message += m => { if (m.BodyCase == ServerMessage.BodyOneofCase.Error) errors.Add(m.Error.Message); };
            Assert.False(c.Connection.Admin);
            c.Connection.Send(new ClientMessage { Speed = new SetSpeed { Speed = 3 } });
            c.Connection.Send(new ClientMessage { NewCity = new NewCityRequest { Name = "Mine now" } });
            WaitFor(c, () => errors.Count >= 2, "the refusals");
            Assert.All(errors, e => Assert.Contains("admins", e));
            token = c.Connection.Credentials!;
            Assert.True(token.Token.Length > 0);
        }

        using var back = new CityClient(_server.Ws, "test", token);
        WaitFor(back, () => back.City.Loaded, "the city after signing in with the token");
        Assert.Equal("Second", back.Connection.Player);
    }

    [Fact]
    public void FiveWrongPasswordsAndTheServerHangsUp()
    {
        using var c = new CityClient(_server.Ws, "test");
        WaitFor(c, () => c.Connection.SignInNeeded, "the welcome");
        bool dropped = false;
        c.Connection.StatusChanged += open => { if (!open) dropped = true; };
        for (int i = 0; i < 5 && !dropped; i++)
        {
            c.Connection.SignIn("Nobody", "wrong" + i);
            WaitFor(c, () => c.Connection.SignInNeeded || dropped, "the refusal");
        }
        WaitFor(c, () => dropped, "the server to hang up");
    }
}

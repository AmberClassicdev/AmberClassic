using System.Net;
using System.Text.Json;
using Amberline.AirCompanion;
using Amberline.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using RtmpSharp.IO;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 800 (AIR 132 E): the store AIR 1.52 asks for beyond the store page. Its champion detail page (championDetails.swf in mod_champ)
/// POSTs its store URL's variables to <c>catalog/getCatalogForUser/&lt;locale&gt;</c> and <c>catalog/purchaseItem</c> and reads JSON
/// (StoreCatalogDao.as:69-77, 115-131); the store opens <c>/store/tabs/view/champions|skins</c> (StoreController.as:31-33). File:line
/// references are into the 1.52 export.
/// </summary>
public static class Air132StoreChecks
{
    // CatalogResult.unmarshall (as:27-43), Catalog.unmarshall (as:101-127) and CatalogItem.unmarshall (as:96-140) read only these keys.
    private static readonly HashSet<string> ResultKeys = ["error", "rp_balance", "catalog", "last_modified", "error_message", "ip_balance", "rental_status"];
    private static readonly HashSet<string> RowKeys = ["BundleItems", "Item", "ItemRentals", "ItemPricingSchedule", "ItemFeaturedSchedule"];
    private static readonly HashSet<string> ItemKeys =
    [
        "champion_owned", "requirements_met", "inventory_type", "is_owned", "type", "effects", "is_entitled", "tier", "original_ip_cost",
        "DaysUntilExpiration", "winCountRemaining", "original_rp_cost", "name", "item_can_be_free", "is_hot", "ItemStatusRank", "is_rentable",
        "is_new", "active", "inactive_date", "is_featured", "id", "release_date", "IsTimedRental", "duration", "sort_order", "ip_cost",
        "description", "is_sale", "ExpirationMessage", "rp_cost", "ExpirationWindow", "end_date", "is_purchasable", "tags", "ItemIsExpiring",
        "IsPerWinRental", "ItemIsExpired", "is_rented", "groups",
    ];

    /// <summary>
    /// The catalog answers the account's balances and one <c>{"Item": ...}</c> row per champion and skin on offer, keyed as the page looks them
    /// up (<c>champions_&lt;id&gt;</c>, <c>championsskin_&lt;championId&gt;&lt;rank:000&gt;</c>), with the store page's ownership and prices and
    /// only keys the client reads; a wrong ticket or a request that is no form is refused in the answer's <c>error</c>.
    /// </summary>
    [Check("server.air132-champion-detail-catalog")]
    public static void ChampionDetailCatalog() => Run(async (store, root) =>
    {
        var accounts = store.Accounts;
        var account = accounts.RegisterPublic("cdpcatalog", "testing-password", "Cdp Catalog", maxed: false);
        accounts.GrantCurrency(account.Id, 20_000, 3_000, Guid.NewGuid());
        accounts.Purchase(account.Id, "champion:1", "IP", Guid.NewGuid());
        var ticket = store.SignIn(account, "cdp-catalog");

        var answer = await store.PostAsync("/catalog/getCatalogForUser/en_US", ("sessionId", ticket));
        var economy = PlatformEconomy.For(accounts.Get(account.Id));
        // Batch 806: the companion relays one answer of at most its response cap (AirRemoteProxy.MaxResponseBytes); the whole catalog must fit.
        TestAssert.True(System.Text.Encoding.UTF8.GetByteCount(answer.GetRawText()) < AirRemoteProxy.MaxResponseBytes,
            "the full catalog answer fits the companion's response cap");
        TestAssert.Equal(0, answer.GetProperty("error").GetInt32());
        TestAssert.Equal(economy.Ip, answer.GetProperty("ip_balance").GetInt64());
        TestAssert.Equal(economy.Rp, answer.GetProperty("rp_balance").GetInt64());
        TestAssert.False(answer.GetProperty("rental_status").GetBoolean(), "true would show every skin card plain (SkinCard.as:235-239)");
        TestAssert.True(answer.EnumerateObject().All(field => ResultKeys.Contains(field.Name)), "CatalogResult reads every top-level key");
        var rows = answer.GetProperty("catalog").EnumerateArray().ToArray();
        TestAssert.True(rows.All(row => row.EnumerateObject().All(field => RowKeys.Contains(field.Name))), "Catalog reads every row key");
        var items = rows.Select(row => row.GetProperty("Item")).ToDictionary(item => item.GetProperty("id").GetString()!);
        TestAssert.True(items.Values.All(item => item.EnumerateObject().All(field => ItemKeys.Contains(field.Name))),
            "CatalogItem reads every item key");
        var offered = accounts.StoreCatalog.Items.Where(item => item.Kind is "CHAMPION" or "CHAMPION_SKIN").ToArray();
        TestAssert.Equal(offered.Length, items.Count, "one row per champion and skin on offer");

        void Same(string id, PlatformStoreItem product, bool owned, bool championOwned)
        {
            var item = items[id];
            TestAssert.Equal(product.Name, item.GetProperty("name").GetString(), id);
            TestAssert.Equal(product.Kind, item.GetProperty("inventory_type").GetString(), id);
            TestAssert.Equal(owned, item.GetProperty("is_owned").GetBoolean(), id + " is_owned");
            TestAssert.Equal(championOwned, item.GetProperty("champion_owned").GetBoolean(), id + " champion_owned");
            TestAssert.True(item.GetProperty("active").GetBoolean(), id + " is on offer");
            TestAssert.Equal(product.IpPrice ?? 0, item.GetProperty("ip_cost").GetInt32(), id + " ip_cost");
            TestAssert.Equal(product.RpPrice ?? 0, item.GetProperty("rp_cost").GetInt32(), id + " rp_cost");
        }
        Same("champions_1", accounts.StoreCatalog.GetChampion(1), owned: true, championOwned: true);
        Same("champions_101", accounts.StoreCatalog.GetChampion(101), owned: false, championOwned: false);
        Same("championsskin_1002", accounts.StoreCatalog.GetSkin(1002), owned: false, championOwned: true);
        Same("championsskin_101001", accounts.StoreCatalog.GetSkin(101001), owned: false, championOwned: false);
        // ChampionSkin.getFullId: "championsskin_" + championId + padNumber(rank, 3) (as:32-35).
        TestAssert.True(items.ContainsKey("championsskin_" + 101 + 1.ToString("000")));
        TestAssert.False(items["champions_1"].TryGetProperty("groups", out _), "a champion is no skin of a champion");
        TestAssert.True(items["championsskin_1002"].GetProperty("groups").GetProperty("champion").TryGetProperty("champions_1", out _),
            "getSkinsForChampion finds Annie's skin by 'champions_1' in groups.champion (CatalogResult.as:58-77)");
        TestAssert.True(items["championsskin_101001"].GetProperty("groups").GetProperty("champion").TryGetProperty("champions_101", out _));

        foreach (var refused in new[]
        {
            await store.PostAsync("/catalog/getCatalogForUser/en_US", ("sessionId", new string('A', 64))),
            await store.PostAsync("/catalog/getCatalogForUser/en_US"),
            await store.PostJsonAsync("/catalog/getCatalogForUser/en_US", "{\"sessionId\":\"" + ticket + "\"}"),
        })
        {
            TestAssert.Equal(1, refused.GetProperty("error").GetInt32(), "StoreCatalogDao throws error_message (as:95-98)");
            TestAssert.False(string.IsNullOrEmpty(refused.GetProperty("error_message").GetString()));
            TestAssert.False(refused.TryGetProperty("catalog", out _));
        }
    });


    /// <summary>
    /// One ticket serves both 1.52 consumers: the store page (cookie session from the URL's sessionId) and the champion detail page (the
    /// sessionId in each POST), each asking getStoreUrl itself, again and again, past the unused-ticket lapse and across an RTMP resume. A new
    /// ticket for the account (a new login, or after an unused one lapsed) revokes the old one for both.
    /// </summary>
    [Check("server.air132-store-ticket-two-consumers")]
    public static void StoreTicketServesBothConsumers() => Run(async (store, root) =>
    {
        var accounts = store.Accounts;
        var sessions = store.Sessions;
        var account = accounts.RegisterPublic("twoconsumers", "testing-password", "Two Consumers", maxed: false);
        accounts.GrantCurrency(account.Id, 20_000, 0, Guid.NewGuid());
        var annie = accounts.StoreCatalog.GetChampion(1);
        var ticket = store.SignIn(account, "rtmp-1");
        TestAssert.Equal(ticket, sessions.IssueStoreTicket("rtmp-1"), "the champion detail page's own getStoreUrl names the store page's ticket");

        async Task<bool> StorePage(string value)
        {
            // A browser without the store cookie: the URL's ticket alone opens the store (PlatformStore's /store/entry).
            using var response = await store.Client.GetAsync("/store/entry?sessionId=" + Uri.EscapeDataString(value));
            return response.StatusCode == HttpStatusCode.Redirect && response.Headers.Contains("Set-Cookie");
        }
        async Task<bool> ChampionDetail(string value) =>
            (await store.PostAsync("/catalog/getCatalogForUser/en_US", ("sessionId", value))).GetProperty("error").GetInt32() == 0;

        TestAssert.True(await StorePage(ticket), "the store page");
        TestAssert.True(await ChampionDetail(ticket), "the champion detail page");
        var bought = await store.PostAsync("/catalog/purchaseItem", ("sessionId", ticket), ("duration", "0"), ("duration_type", "PURCHASED"),
            ("quantity", "1"), ("item_id", "champions_1"), ("currency_type", "ip"), ("rp", (annie.RpPrice ?? 0).ToString()),
            ("ip", annie.IpPrice!.Value.ToString()));
        TestAssert.Equal(0, bought.GetProperty("error").GetInt32(), "the champion detail page buys with it too");
        TestAssert.True(await StorePage(ticket), "the store page opens with it again");

        store.Clock.Advance(TimeSpan.FromMinutes(10));
        TestAssert.True(await ChampionDetail(ticket), "a used ticket outlives the two-minute lapse while its login lives");

        var token = sessions.Require("rtmp-1").Token;
        sessions.Suspend("rtmp-1");
        TestAssert.False(await ChampionDetail(ticket), "not usable while the login is parked");
        TestAssert.True(sessions.Resume("rtmp-2", account.Username, token));
        TestAssert.True(await ChampionDetail(ticket), "the resumed login keeps its ticket");
        TestAssert.Equal(ticket, sessions.IssueStoreTicket("rtmp-2"));

        var reissued = store.SignIn(account, "rtmp-3");
        TestAssert.False(ticket == reissued, "a new login gets a new ticket");
        TestAssert.False(await StorePage(ticket), "the store page refuses the revoked ticket");
        TestAssert.False(await ChampionDetail(ticket), "the champion detail page refuses the revoked ticket");
        TestAssert.True(await ChampionDetail(reissued) && await StorePage(reissued), "both take the new one");

        var unused = store.SignIn(account, "rtmp-4");
        TestAssert.False(await ChampionDetail(reissued), "the older login's ticket is revoked");
        store.Clock.Advance(TimeSpan.FromMinutes(3));
        TestAssert.False(await StorePage(unused), "an unused ticket lapses after two minutes (store page)");
        TestAssert.False(await ChampionDetail(unused), "an unused ticket lapses after two minutes (champion detail page)");
        var next = sessions.IssueStoreTicket("rtmp-4");
        TestAssert.False(unused == next, "a lapsed ticket is replaced");
        TestAssert.True(await ChampionDetail(next) && await StorePage(next));
    });

    /// <summary>
    /// <c>/store/tabs/view/champions</c> and <c>/store/tabs/view/skins</c> (StoreController.as:31-33; LobbyController.refreshStoreView,
    /// as:569-577) are the store page's lists of that kind, opened by the store cookie or the URL's ticket; no other tab is one.
    /// </summary>
    [Check("server.air132-store-tab-routes")]
    public static void StoreTabRoutes() => Run(async (store, root) =>
    {
        var accounts = store.Accounts;
        var account = accounts.RegisterPublic("storetabs", "testing-password", "Store Tabs", maxed: false);
        var ticket = store.SignIn(account, "tabs");
        var access = store.Sessions.RedeemStoreTicket(ticket);
        async Task<(HttpStatusCode Status, string Html)> Get(string path, bool cookie)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (cookie) request.Headers.Add("Cookie", PlatformStore.StoreCookie + "=" + access.Token);
            using var response = await store.Client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        // The client's URL: the store base, the relative path, then the canonical URL's variables (StoreController.getCurrentStoreUrl).
        var query = "?sessionId=" + Uri.EscapeDataString(ticket) + "&lang=en_US";
        var tabs = new[] { ("champions", "CHAMPION", "c-champion-1", "c-skin-1002"), ("skins", "CHAMPION_SKIN", "c-skin-1002", "c-champion-1") };
        foreach (var (tab, kind, _, _) in tabs)
        {
            var (status, html) = await Get("/store/tabs/view/" + tab + query, cookie: true);
            TestAssert.Equal(HttpStatusCode.OK, status, tab);
            TestAssert.Equal(PlatformStore.RenderHome(access, accounts, kind), html, tab + " is the store page's list of " + kind);
        }
        // Without the cookie the URL's ticket opens it (a new store session; the cookie's ends).
        foreach (var (tab, _, card, other) in tabs)
        {
            var (status, html) = await Get("/store/tabs/view/" + tab + query, cookie: false);
            TestAssert.Equal(HttpStatusCode.OK, status, tab + " opens from the URL's ticket");
            TestAssert.True(html.Contains("id=\"" + card + "\"", StringComparison.Ordinal) && !html.Contains("id=\"" + other + "\"", StringComparison.Ordinal), tab);
        }
        TestAssert.Equal(HttpStatusCode.NotFound, (await Get("/store/tabs/view/runes" + query, cookie: true)).Status, "only the two tabs 1.52 names");
        TestAssert.Equal(HttpStatusCode.BadRequest, (await Get("/store/tabs/view/skins", cookie: false)).Status, "no cookie, no ticket");
    });

    private sealed class Store(PlatformAccounts accounts, PlatformSessions sessions, ManualTimeProvider clock, HttpClient client)
    {
        private readonly List<(long AccountId, AsObject Data)> _pushes = [];
        public PlatformAccounts Accounts { get; } = accounts;
        public PlatformSessions Sessions { get; } = sessions;
        public ManualTimeProvider Clock { get; } = clock;
        public HttpClient Client { get; } = client;

        public void Push(long account, object body) { lock (_pushes) _pushes.Add((account, (AsObject)body)); }

        public List<(long AccountId, AsObject Data)> TakePushes()
        {
            lock (_pushes)
            {
                var taken = _pushes.ToList();
                _pushes.Clear();
                return taken;
            }
        }

        /// <summary>A login on a channel-authenticated connection, as the stock client's RTMP login makes one, and its getStoreUrl ticket.</summary>
        public string SignIn(PlatformAccount account, string connection)
        {
            var session = Sessions.Login(connection, account.Username, Sessions.IssueTicket(account), AirLine.ClientVersion);
            TestAssert.True(Sessions.AuthenticateChannel(connection, account.Username, session.Token));
            return Sessions.IssueStoreTicket(connection);
        }

        public Task<JsonElement> PostAsync(string path, params (string Name, string Value)[] fields) =>
            SendAsync(path, new FormUrlEncodedContent(fields.Select(field => KeyValuePair.Create(field.Name, field.Value))));

        public Task<JsonElement> PostJsonAsync(string path, string json) =>
            SendAsync(path, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        private async Task<JsonElement> SendAsync(string path, HttpContent content)
        {
            using (content)
            using (var response = await Client.PostAsync(path, content))
            {
                var text = await response.Content.ReadAsStringAsync();
                TestAssert.Equal(HttpStatusCode.OK, response.StatusCode, path + " answers 200 with its JSON: " + text);
                using var document = JsonDocument.Parse(text);
                return document.RootElement.Clone();
            }
        }
    }

    // The platform with the store page and 1.52's routes, the art of two skins (Annie's 1002, Xerath's 101001) and a hand-moved clock.
    private static void Run(Func<Store, string, Task> body) => RunAsync(body).GetAwaiter().GetResult();

    private static async Task RunAsync(Func<Store, string, Task> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air132-store-" + Guid.NewGuid().ToString("N"));
        var art = Path.Combine(root, "assets", "storeImages", "content", "skins");
        Directory.CreateDirectory(art);
        foreach (var id in new[] { 1002, 101001 }) File.WriteAllBytes(Path.Combine(art, $"championsskin_{id}.jpg"), [0xff, 0xd8, 0xff, 0xd9]);
        try
        {
            using var accounts = new PlatformAccounts(root, root);
            var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
            var sessions = new PlatformSessions(accounts, clock);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            await using var app = builder.Build();
            // As the platform's own middleware: a store refusal is a 400 page.
            app.Use(async (context, next) =>
            {
                try { await next(context); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException)
                { context.Response.StatusCode = StatusCodes.Status400BadRequest; }
            });
            Store? store = null;
            PlatformStore.Map(app, accounts, sessions, root, (account, value) => store!.Push(account, value));
            PlatformStore.Map152(app, accounts, sessions, (account, value) => store!.Push(account, value));
            await app.StartAsync();
            try
            {
                using var handler = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false, UseProxy = false };
                using var client = new HttpClient(handler) { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(30) };
                store = new Store(accounts, sessions, clock, client);
                await body(store, root);
            }
            finally { await app.StopAsync(); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private sealed class ManualTimeProvider(DateTimeOffset current) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = current;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan amount) => Now += amount;
    }
}

using System.Globalization;
using System.Text.Json;

namespace Amberline.Platform;

/// <summary>
/// The store routes only AIR 1.52 asks for (132 line; LolClient 1.45 names none of these paths), beside the shared store page of
/// <see cref="Map"/>. File:line references are into the 1.52 export.
/// <para>
/// The champion detail page (mod_champ hosting <c>assets/championDetail/championDetails.swf</c>) takes the URL of
/// <c>loginService.getStoreUrl</c> (ChampionDetailInterface.as:174-233) and splits it into a base and its query variables (StoreConfig.as:35-56;
/// here <c>http://127.0.0.1:&lt;port&gt;/</c> and <c>sessionId</c>). It POSTs those variables as a form to <c>catalog/getCatalogForUser/&lt;locale&gt;</c>
/// and <c>catalog/purchaseItem</c> (StoreCatalogDao.as:69-77, 115-131) and reads JSON back. Its ticket is the store's own
/// (<see cref="PlatformSessions.RequireStoreTicket"/>); a refusal is the answer's <c>error</c> and <c>error_message</c> (as:95-98, 149-152).
/// </para>
/// </summary>
public static partial class PlatformStore
{
    // Field names exactly as the client reads them (Item, rp_balance ...): no naming policy.
    private static readonly JsonSerializerOptions WireNames = new();

    /// <summary>Register 1.52's own store routes. <paramref name="notify"/> is the store page's push (fulfillment and balance).</summary>
    public static void Map152(WebApplication app, PlatformAccounts accounts, PlatformSessions sessions, Action<long, object>? notify = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(sessions);
        // ponytail: its own gate, so a purchase here and one on the store page could interleave their balance pushes; one player clicks
        // one of the two at a time. Sharing the store page's gate needs a host change.
        var purchaseGate = new object();

        // Reconstruction: the locale picked retail's translations (not recovered); this catalog's names are the game data's own, so it is not read.
        app.MapPost("/catalog/getCatalogForUser/{locale}", (Delegate)((HttpContext context) => ChampionDetailAsync(context, sessions, (accountId, _) =>
            CatalogForUser(accounts, accounts.Get(accountId)))));

        app.MapPost("/catalog/purchaseItem", (Delegate)((HttpContext context) => ChampionDetailAsync(context, sessions, (accountId, form) =>
        {
            // PurchaseItemParams (as:6-14) and StoreCatalogDao.as:119-125: one item, bought outright, in "ip" or "rp".
            // champions_<id> or championsskin_<id>, the store page's view_item names (ChampionDetails.as:254, ChampionSkin.as:32-35).
            var named = form["item_id"].ToString();
            var itemId = StockProductId("Champion", named) ?? StockProductId("Skin", named)
                ?? throw new ArgumentException("The store purchase request is invalid.");
            var currency = form["currency_type"].ToString().ToUpperInvariant();
            if (currency is not ("IP" or "RP")) throw new ArgumentException("The store currency is invalid.");
            if (form["quantity"].ToString() != "1" || form["duration_type"].ToString() != "PURCHASED")
                throw new ArgumentException("The store purchase request is invalid.");
            // The price the page showed (its catalog row's ip_cost or rp_cost, as:124-125). As the store page's expectedPrice it is only
            // compared with the server's own price, never used as one.
            var shown = long.TryParse(form[currency == "IP" ? "ip" : "rp"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var price)
                ? price : throw new ArgumentException("The store purchase request is invalid.");
            lock (purchaseGate)
            {
                // The store page's purchase (PlatformAccounts.Purchase: ownership, level, balance, the charge) and its two pushes.
                // Reconstruction: the page sends no request id and retail's guard against a repeat is not recovered; a repeat is refused
                // as already owned, since this route sells only champions and skins.
                var purchase = accounts.Purchase(accountId, itemId, currency, Guid.NewGuid(), 1, shown);
                var account = accounts.Get(accountId);
                var item = accounts.StoreCatalog.TryGet(itemId, out var offered) ? offered : accounts.StoreCatalog.Get(itemId);
                var rpOff = accounts.Controls.PaidContentOff;
                if (notify is not null)
                {
                    notify(account.Id, Fulfillment(item, account, PlatformEconomy.For(account), purchase.Quantity, rpOff));
                    notify(account.Id, AirDtos.AccountBalanceNotification(account, rpOff));
                }
            }
            return new Dictionary<string, object> { ["error"] = 0 };
        })));

        // StoreConfig.getRpPurchaseUrl (as:63-70): the champion detail page's Purchase RP opens storeUrl + "store/points/purchase?" and its
        // variables in the client's internal browser (PurchaseScreen.as:225-234, DialogController.as:211-219). It is the store page's own
        // RP page (/store/rp). NOT RETAIL: retail sold RP for money there; payment never happens here, the host grants RP (PlatformStore).
        app.MapGet("/store/points/purchase", (HttpContext context) =>
        {
            var access = RequireOrOpen(context, sessions);
            if (accounts.Controls.PaidContentOff) return Results.Redirect("/store/home");
            return Results.Content(RenderRpInformation(accounts.Get(access.AccountId)), "text/html; charset=utf-8");
        });

        // StoreController.as:31-33: after a champion detail page purchase the client reloads the store at its champions or skins tab
        // (DialogController.as:688-692, LobbyController.as:569-577), the store URL's variables after the path. The tab is the store
        // page's list of that kind.
        foreach (var (tab, kind) in new[] { ("champions", "CHAMPION"), ("skins", "CHAMPION_SKIN") })
            app.MapGet("/store/tabs/view/" + tab, (HttpContext context) =>
                Results.Content(RenderHome(RequireOrOpen(context, sessions), accounts, kind), "text/html; charset=utf-8"));
    }

    private static async Task<IResult> ChampionDetailAsync(HttpContext context, PlatformSessions sessions, Func<long, IFormCollection, object> answer)
    {
        try
        {
            // URLRequest with URLVariables data and POST: a urlencoded form (StoreCatalogDao.as:72-74, 126-127).
            var form = context.Request.HasFormContentType ? await context.Request.ReadFormAsync(context.RequestAborted)
                : throw new ArgumentException("The store request is invalid.");
            var ticket = form["sessionId"].ToString();
            if (ticket.Length == 0) throw new ArgumentException("Open the store from the AIR client.");
            return Results.Json(answer(sessions.RequireStoreTicket(ticket), form), WireNames);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return Results.Json(new Dictionary<string, object> { ["error"] = 1, ["error_message"] = error.Message }, WireNames);
        }
    }

    /// <summary>
    /// <c>getCatalogForUser</c>'s answer (CatalogResult.unmarshall, as:27-43): the account's balances and one row per champion and skin on
    /// offer now, each <c>{"Item": ...}</c> (Catalog.unmarshall, as:101-127). Reconstruction: retail's full answer (its other CatalogItem
    /// fields, rentals, schedules, which products it listed) is not recovered; only the fields the page reads are sent, the others take
    /// the client's defaults, and only champions and skins are listed (the page's two inventory types, InventoryTypesEnum.as).
    /// </summary>
    private static Dictionary<string, object> CatalogForUser(PlatformAccounts accounts, PlatformAccount account)
    {
        var economy = PlatformEconomy.For(account);
        var catalog = accounts.StoreCatalog;
        var rows = catalog.Items.Where(item => item.Kind is "CHAMPION" or "CHAMPION_SKIN")
            .Select(item => new Dictionary<string, object> { ["Item"] = CatalogItem(item, account, economy, catalog) }).ToArray();
        return new Dictionary<string, object>
        {
            ["error"] = 0,
            ["rp_balance"] = AirDtos.ShownRp(economy, accounts.Controls.PaidContentOff),
            ["ip_balance"] = economy.Ip,
            // Reconstruction: retail's value is not recovered; true would show every skin card plain (SkinCard.as:235-239), with no price
            // or owned state, and this server rents nothing.
            ["rental_status"] = false,
            ["catalog"] = rows,
        };
    }

    /// <summary>
    /// One CatalogItem (as:96-140) as the page reads it: <c>id</c> (ChampionDetails.as:254, ChampionSkin.getFullId as:32-35), <c>name</c>,
    /// <c>inventory_type</c>, <c>is_owned</c>, <c>champion_owned</c> and <c>active</c> (SkinCard.as:240-270), the costs (PurchaseScreen.as:297-378;
    /// 0 is no price in that currency: its IP card is hidden), and for a skin its champion in <c>groups.champion</c>, which a champion's
    /// purchase reads to mark the skins (CatalogResult.getSkinsForChampion, as:58-77; PurchaseScreen.as:274-280). Ownership is the store page's.
    /// </summary>
    private static Dictionary<string, object> CatalogItem(PlatformStoreItem item, PlatformAccount account, PlatformEconomyState economy,
        PlatformStoreCatalog catalog)
    {
        var champion = item.Kind == "CHAMPION";
        var owned = IsOwned(item, account, economy, catalog);
        var row = new Dictionary<string, object>
        {
            ["id"] = (champion ? "champions_" : "championsskin_") + item.ContentId.ToString(CultureInfo.InvariantCulture),
            ["name"] = item.Name,
            ["inventory_type"] = item.Kind,
            ["is_owned"] = owned,
            ["champion_owned"] = champion ? owned : economy.AllUnlocked || economy.OwnedChampions?.Contains(item.ChampionId) == true,
            ["active"] = !item.Locked,
            ["ip_cost"] = item.IpPrice ?? 0,
            ["rp_cost"] = item.RpPrice ?? 0,
        };
        if (!champion)
            row["groups"] = new Dictionary<string, object>
            {
                ["champion"] = new Dictionary<string, object> { ["champions_" + item.ChampionId.ToString(CultureInfo.InvariantCulture)] = true },
            };
        return row;
    }
}

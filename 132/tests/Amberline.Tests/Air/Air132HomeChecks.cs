using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Amberline.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 813 (132 Home, January 2012): the operator folder <c>assets/air-home-2012</c> that the install seeds into <c>content/home</c>.
/// The folder half (News rows, the store notice, the images, sources.json) reads the files directly. The rendering half needs the
/// Platform's own content root, which is read once per process (<c>AMBERLINE_PLATFORM_CONTENT</c>), so it runs in a child process of this
/// test runner whose content root holds a copy of the folder under <c>home/</c>.
/// </summary>
public static class Air132HomeChecks
{
    private const string ChildVariable = "AMBERLINE_B813_HOME_CHILD";

    // The Platform's allow-list of the operator's page images (PlatformHome.BundledName).
    private static readonly Regex BundledName = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}\\.(png|jpg|gif)$", RegexOptions.CultureInvariant);

    // The page's own stock art: the Platform serves these from the shipped client, not from the folder.
    private static readonly Regex StockKey = new("^(background|parchment|(feature|portrait)-[0-9]+)$", RegexOptions.CultureInvariant);

    private static readonly DateTimeOffset FirstDay = new(2011, 12, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndOfJanuary = new(2012, 2, 1, 0, 0, 0, TimeSpan.Zero);


    // The rendering half, in the child: the Platform's own pages and images, served from the copy of the folder under home/.
    private static async Task RenderAsync(string folder, string[] images)
    {
        TestAssert.True(PlatformContent.Has("home/landing.html") && PlatformContent.Has("home/home.json") && PlatformContent.Has("home/story.html") == File.Exists(Path.Combine(folder, "story.html")),
            "the content root serves the folder as home/");
        var home = PlatformHome.RenderLanding(PlatformHomeModel.Testing(1));
        TestAssert.True(home.Contains("News</h2>", StringComparison.Ordinal), "the Home has its News panel");
        using (var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "home.json"))))
            foreach (var row in json.RootElement.GetProperty("news").EnumerateArray())
            {
                var title = WebUtility.HtmlEncode(row.GetProperty("title").GetString()!);
                var date = row.GetProperty("publishedAt").GetDateTimeOffset().ToString("MM/dd", CultureInfo.InvariantCulture);
                TestAssert.True(home.Contains(title, StringComparison.Ordinal) && home.Contains(">" + date + "<", StringComparison.Ordinal),
                    title + " is shown with its date " + date);
            }
        var rss = XDocument.Parse(PlatformHome.RenderRss("store")).Descendants("item").Single();
        var pubDate = DateTimeOffset.Parse(rss.Element("pubDate")!.Value, CultureInfo.InvariantCulture);
        TestAssert.True(pubDate.Year == 2012 && pubDate.Month == 1 && pubDate.Day == 17, "the store notice's pubDate is 17 January 2012");

        // Page art is self-hosted: every src and url() is a same-origin path (or the two font faces of the companion's loopback server).
        foreach (Match match in Regex.Matches(home, "(?:src=\"|url\\(')(?<url>[^\"')]+)"))
            TestAssert.True(match.Groups["url"].Value.StartsWith('/') ||
                match.Groups["url"].Value.StartsWith("http://127.0.0.1:8396/fonts/friz", StringComparison.Ordinal),
                "page art must be self-hosted: " + match.Groups["url"].Value);

        // Every image the page names is a folder image on the allow-list, served with the archived bytes.
        var referenced = Regex.Matches(home, "/img/home/(?<name>[^'\"\\s)]+)").Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal).ToArray();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        PlatformHome.MapImages(app, folder);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            foreach (var name in referenced)
            {
                if (StockKey.IsMatch(name)) continue;
                TestAssert.True(images.Contains(name) && BundledName.IsMatch(name), name + " is a folder image on the allow-list");
                using var response = await client.GetAsync("/img/home/" + name);
                TestAssert.Equal(HttpStatusCode.OK, response.StatusCode, name);
                TestAssert.Equal(File.ReadAllBytes(Path.Combine(folder, name)), await response.Content.ReadAsByteArrayAsync(), name + ": the archived bytes");
            }
        }
        finally { await app.StopAsync(); }

        if (PlatformContent.Has("home/story.html"))
            TestAssert.True(PlatformHome.RenderStory().Contains("Journal of Justice", StringComparison.Ordinal), "the Journal page is the operator's");
        else
            TestAssert.True(PlatformHome.RenderStory().Contains("<h1>Journal</h1>", StringComparison.Ordinal), "no January Journal: the neutral page");
    }

}

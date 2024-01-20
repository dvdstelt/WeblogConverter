using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Html2Markdown;
using HtmlAgilityPack;
using WordPressPCL;
using WordPressPCL.Models;
using WordPressPCL.Utility;
using WordpressToJekyll;

var blogPath = @"c:\temp\blog";
var physicalPostsPath = Path.Combine(blogPath, "_posts");
var physicalImagesPath = Path.Combine(blogPath, "images");

if (!Directory.Exists(physicalPostsPath)) Directory.CreateDirectory(physicalPostsPath);
if (!Directory.Exists(physicalImagesPath)) Directory.CreateDirectory(physicalImagesPath);

var baseUri = new Uri("https://bloggingabout.net/");
var newImagesUri = new Uri(baseUri, "/images/");
string[] knownLanguages = { "csharp", "xml", "json", "powershell", "plain" };

var markdownConverter = new Converter();
var doc = new HtmlDocument();
var wordPressClient = new WordPressClient("https://bloggingabout-linux.azurewebsites.net/wp-json/");

Console.WriteLine("Retrieving categories...");
var categories = await wordPressClient.Categories.GetAllAsync();
Console.WriteLine("Retrieving tags...");
var tags = await wordPressClient.Tags.GetAllAsync();

var githubComments = new GithubComments(markdownConverter);
await githubComments.Initialize();

const bool doComments = true;
const bool doPosts = false;

// await DoOnePost(578616); // Partitioning data through events
// await DoOnePost(578788); // NServiceBus Publish Subscribe tutorial
// await DoOnePost(578363); // Colored console tracelistener for Enterprise Library 5.0
// await DoOnePost(63016);  // WCF decision chart
// await DoOnePost(579114); // Amazon Prime
// await DoOnePost(459767); // VS2008 DataDude (code all over html)
// await DoOnePost(476380); // Oslo: Building textural DSLs (code all over html)
// await DoOnePost(579017); // Priority queues (with 4 comments)
// await DoOnePost(578023); // High availability (with comments)

await DoAllPosts();

return;

async Task DoOnePost(int postId)
{
    Console.WriteLine($"Processing post {postId}");

    var post = await wordPressClient.Posts.GetByIDAsync(postId);
    if (doPosts) await ProcessPost(post);
    if (doComments) await ProcessComments(post);
}

async Task DoAllPosts()
{
    var page = 47;

    while (true)
    {
        Console.WriteLine($"Page {page} of blogposts");

        var queryBuilder = new PostsQueryBuilder();
        queryBuilder.PerPage = 10;
        queryBuilder.Page = page;
        var posts = await wordPressClient.Posts.QueryAsync(queryBuilder);

        foreach (var post in posts)
        {
            if (post.Id == 476380)
            {
                Console.WriteLine("breakpoint");
            }

            if (doPosts) await ProcessPost(post);
            if (doComments) await ProcessComments(post);
        }

        if (posts.Count() < 10)
            return;

        page++;
    }
}

async Task ProcessPost(Post post)
{
    Console.WriteLine($"  {post.Id} - {markdownConverter.Convert(post.Title.Rendered)}");

    //
    // *** Convert any syntaxhighlighter blocks
    //
    doc.LoadHtml(post.Content.Rendered);
    var htmlCodeNodes = doc.DocumentNode
        .SelectNodes(
            "//div[contains(@class,'wp-block-syntaxhighlighter-code')] | //pre[contains(@class, 'brush')]")
        ?.ToList();
    //
    foreach (var node in htmlCodeNodes ?? Enumerable.Empty<HtmlNode>())
    {
        var language = "csharp"; // default
        Match match = Regex.Match(node.OuterHtml, @"brush:\s*([^;]+)");
        if (match.Success)
        {
            if (!knownLanguages.Contains(match.Groups[1].Value))
                throw new Exception($"Unfamiliar language: {match.Groups[1].Value}");
            language = match.Groups[1].Value;
        }

        var newNode = HtmlNode.CreateNode($"<code>\r\n{language}\r\n" + node.InnerText.Trim() + "\r\n</code>\r\n");
        node.ParentNode.ReplaceChild(newNode, node);
    }

    //
    // *** Convert weird image html stuff
    //
    var wordPressImages = doc.DocumentNode
        .SelectNodes("//div[contains(@class, 'wp-block-image')] | //figure[contains(@class, 'wp-block-image')]")
        ?.ToList();
    foreach (var node in wordPressImages ?? Enumerable.Empty<HtmlNode>())
    {
        // We found the div we're looking for, now let's look into its children to find the actual image
        var imgNode = node.SelectNodes(".//img").First();
        node.ParentNode.ReplaceChild(imgNode, node);
    }

    //
    wordPressImages = doc.DocumentNode.SelectNodes("//img")?.ToList();
    foreach (var node in wordPressImages ?? Enumerable.Empty<HtmlNode>())
    {
        string srcsetAttribute = node.GetAttributeValue("srcset", "");

        if (!string.IsNullOrEmpty(srcsetAttribute))
        {
            var imgUrl = node.Attributes["src"].Value;
            var sources = srcsetAttribute.Split(',');

            // Parse each source to extract the URL and size
            var imageInfo = sources
                .Select(source =>
                {
                    var parts = source.Trim().Split(' ');
                    if (parts.Length == 2 && parts[1].EndsWith("w"))
                    {
                        int size = int.Parse(parts[1].TrimEnd('w'));
                        return new { Url = parts[0], Size = size };
                    }

                    return null;
                })
                .Where(info => info != null)
                .OrderByDescending(info => info.Size)
                .FirstOrDefault();

            if (imageInfo != null)
            {
                imgUrl = imageInfo.Url;
            }

            node.Attributes["src"].Value = imgUrl;
        }
    }

    // Find all links
    var links = doc.DocumentNode.SelectNodes("//a")?.ToList();
    foreach (var node in links ?? Enumerable.Empty<HtmlNode>())
    {
        // Convert very, very old urls to the new format
        // Old urls were: https://bloggingabout-linux.azurewebsites.net/blogs/dennis/archive/2004/01/01.aspx
        string pattern =
            @"https://bloggingabout-linux\.azurewebsites\.net/blogs/dennis/archive/(?<date>\d{4}/\d{2}/\d{2}/[^/]+)\.aspx";
        string transformedUrl =
            Regex.Replace(node.Attributes[0].Value, pattern, "https://bloggingabout.net/${date}");
        node.Attributes[0].Value = transformedUrl;

        Console.WriteLine(node.Attributes["href"].Value);
    }

    //
    // Some sourcecode is literally loaded with HTML. Let's find it and kill it.
    // The following string that we search for is syntax highlighting code that was put there in the past. We now have codeblocks in markdown.
    //
    var uglyCodeNodes = doc.DocumentNode.SelectNodes(
            "//div[contains(@style, 'border-right: #cccccc 1pt solid;padding-right: 1pt;border-top: #cccccc 1pt solid;padding-left: 1pt;font-size: 10pt;background: #f5f5f5;padding-bottom: 1pt;overflow: auto;border-left: #cccccc 1pt solid;width: 100%;color: black;padding-top: 1pt;border-bottom: #cccccc 1pt solid;font-family: lucida console')]")
        ?.ToList();
    foreach (var node in uglyCodeNodes ?? Enumerable.Empty<HtmlNode>())
    {
        var raw = node.OuterHtml;
        raw = raw.Replace("&#8220;", "\"");
        raw = raw.Replace("&#8216;", "'");
        raw = raw.Replace("&#8217;", "'");
        raw = Regex.Replace(raw, "<.*?>", String.Empty);
        raw = HttpUtility.HtmlDecode(raw);
        raw = raw.Replace("\u00a0", " ");

        var newNode = HtmlNode.CreateNode($"\r\n<code>\r\ncsharp\r\n" + raw.Trim() + "\r\n</code>\r\n");
        node.ParentNode.ReplaceChild(newNode, node);
    }

    // Now remove some HTML that creates weird lists when converting to markdown
    var document = doc.DocumentNode.OuterHtml;
    if (post.Id == 477173)
    {
        // This is a weird one!
        document = document.Replace("<ol>", "");
        document = document.Replace("</ol>", "");
        document = document.Replace("<li>", "");
        document = document.Replace("</li>", "");
    }

    var markdown = markdownConverter.Convert(document);

    // Load rest of the stuff after converting to markdown
    doc.LoadHtml(markdown);

    //
    // *** Convert old notes into block-quote
    //
    var noteNodes = doc.DocumentNode.Descendants("div")
        .Where(s => s.Attributes["class"]?.Value is "is-layout-flow wp-block-group"
            or "is-layout-flow wp-block-group ")
        ?.ToList();
    foreach (var node in noteNodes ?? Enumerable.Empty<HtmlNode>())
    {
        var newNode = HtmlNode.CreateNode("> " + node.InnerText.Trim());
        node.ParentNode.ReplaceChild(newNode, node);
    }

    // And back into markdown again
    markdown = doc.DocumentNode.OuterHtml;
    foreach (var language in knownLanguages)
    {
        markdown = markdown.Replace(@"```" + Environment.NewLine + language, Environment.NewLine + "```" + language);
    }

    // Find unsorted lists and make sure empty lines in them are removed.
    // Otherwise the markdown contains way too much whitespace.
    markdown = Regex.Replace(markdown, @"^\s*\n(?=\s*\*)", string.Empty, RegexOptions.Multiline);
    // Remove spaces after x. or indentation will be weird
    markdown = Regex.Replace(markdown, @"\*\s+", "* ");
    // Find sorted lists and make sure empty lines in them are removed.
    markdown = Regex.Replace(markdown, @"^\s*\n(?=\s*\d+\.)", string.Empty, RegexOptions.Multiline);
    markdown = Regex.Replace(markdown, @"\d+\.\s+", match => match.Value.Substring(0, match.Value.Length - 1));

    //
    // Extract stupid Technorati tags and add them to a list. Later we'll create proper tags out of them.
    //
    var extractedTechnoratiTags = new List<string>();
    MatchCollection matches = Regex.Matches(markdown, @"(?i)<div.*?>Technorati tags: (.*?)(?:</div>|</div>.*?)",
        RegexOptions.Singleline);
    //
    foreach (Match match in matches)
    {
        // Extract the matched tags and store them in the list
        string matchedTags = match.Groups[1].Value;

        // Extract individual tags enclosed in square brackets
        MatchCollection tagMatches = Regex.Matches(matchedTags, @"\[(.*?)\]");

        foreach (Match tagMatch in tagMatches)
        {
            extractedTechnoratiTags.Add(tagMatch.Groups[1].Value);
        }
    }

    // Now remove the entire line.
    markdown = Regex.Replace(markdown, @"(?i).*Technorati tags.*", "");

    //
    // Let's start building our post!
    //
    var sb = new StringBuilder();
    sb.AppendLine("---");
    sb.AppendLine("layout: post");
    sb.AppendLine($"id: {post.Id}");
    sb.AppendLine("author: Dennis van der Stelt");

    //
    // Download feature image and add it to the header of the post
    //
    var newImagesFolder = Path.Combine(physicalImagesPath, post.Slug);
    var newImagePathUri = new Uri(newImagesUri, post.Slug + "/");

    if (post.FeaturedMedia != null && post.FeaturedMedia != 0)
    {
        var media = await wordPressClient.Media.GetByIDAsync(post.FeaturedMedia);
        var fileExt = Path.GetExtension(media.SourceUrl);
        var originalFile = new Uri(baseUri, media.SourceUrl);
        // Make up the new stuff

        var newFileLocation = Path.Combine(newImagesFolder, "header" + fileExt);

        // Download the actual file
        await DownloadFile(newFileLocation, originalFile);

        sb.AppendLine($"image: '/images/{post.Slug}/header{fileExt}'");
    }

    //
    // *** Take all images and convert the syntax to markdown [](filename.ext)
    //
    var downloadFiles = new Dictionary<string, string>();
    markdown = Regex.Replace(markdown, @"!\[(.*?)\]\((.*?)\)", m =>
    {
        var fileFromMarkdown = m.Groups[2].ToString();
        fileFromMarkdown = Regex.Replace(fileFromMarkdown, @"\""[^\""]*\""(\s*)$", string.Empty).Trim();
        var fileName = Path.GetFileName(fileFromMarkdown);
        var originalFile = new Uri(baseUri, fileFromMarkdown);
        var newDownloadLocation = Path.Combine(newImagesFolder, fileName);
        var newImageUri = new Uri(newImagePathUri, fileName);

        downloadFiles.TryAdd(originalFile.ToString(), newDownloadLocation);

        return string.Format($"![{m.Groups[1]}]({newImageUri.AbsolutePath})");
    });

    //
    // Now download all the images we found
    //
    foreach (var file in downloadFiles)
    {
        var fileToDownload = file.Key;
        var fileOnDisk = file.Value;

        // This is for older bloggingabout.net urls that I need to convert
        fileToDownload = fileToDownload.Replace("-linux.azurewebsites", "");
        fileToDownload = fileToDownload.Replace("/sites/2", "");

        // When there's alt-text for the image, html2markdown also adds it to the url.
        // This is proper markdown, but I can't handle it ;-)
        if (fileToDownload.IndexOf("\"", StringComparison.Ordinal) > 0)
        {
            var index = fileToDownload.IndexOf(" ", StringComparison.Ordinal);
            fileToDownload = fileToDownload.Substring(0, index);
            index = fileOnDisk.IndexOf(" ", StringComparison.Ordinal);
            fileOnDisk = fileOnDisk.Substring(0, index);
        }

        // For when the querystring of the url contains stuff
        if (fileOnDisk.IndexOf("?", StringComparison.Ordinal) > 0)
        {
            var index = fileOnDisk.IndexOf("?", StringComparison.Ordinal);
            fileOnDisk = fileOnDisk.Substring(0, index);
        }

        // Download the actual file
        // For some weird reason, 474953 stays stuck forever on HttpClient calls to a file
        if (!File.Exists(fileOnDisk) && post.Id != 474953)
        {
            await DownloadFile(fileOnDisk, new Uri(fileToDownload));
        }
    }

    sb.AppendLine($"date: {post.Date.ToString("yyyy-MM-dd hh:mm:ss").PrepareForMarkdown()}");
    sb.AppendLine($"title: {markdownConverter.Convert(post.Title.Rendered).PrepareForMarkdown()}");
    var excerpt = markdownConverter.Convert(post.Excerpt.Rendered);
    if (excerpt.Length > 85) excerpt = excerpt[..85];
    sb.AppendLine($"description: {excerpt.PrepareForMarkdown()}..."); // Specially for the theme
    post.Categories.Remove(1); // Remove 'uncategorized' category
    if (post.Categories.Count > 0)
    {
        sb.AppendLine("categories:");
        foreach (var category in post.Categories)
        {
            sb.AppendLine($"    - {categories.First(s => s.Id == category).Name.PrepareForMarkdown()}");
        }
    }

    // Convert all the tags _AND_ potential Technorati tags that we found
    if (post.Tags.Count > 0 || extractedTechnoratiTags.Count > 0)
    {
        sb.AppendLine("tags:");
        foreach (var tag in post.Tags)
        {
            sb.AppendLine("  - " + tags.First(s => s.Id == tag).Name.PrepareForMarkdown());
        }

        foreach (var tag in extractedTechnoratiTags)
        {
            sb.AppendLine("  - " + tag.PrepareForMarkdown());
        }
    }

    // Add redirects for old urls. Jekyl creates an actual html file for each of them, but who cares.
    sb.AppendLine("redirect_from:");
    sb.AppendLine($"  - \"/dennis/{post.Date.ToString("yyyy/MM/dd")}/{post.Slug}\"");
    sb.AppendLine($"  - \"/blogs/dennis/archive/{post.Date.ToString("yyyy/MM/dd")}/{post.Slug}.aspx\"");
    sb.AppendLine("---\n");
    sb.AppendLine(markdown);

    var filename = post.Date.ToString("yyyy-MM-dd") + "-" + post.Slug + ".md";
    await using var outputFile = new StreamWriter(Path.Combine(physicalPostsPath, filename));
    await outputFile.WriteAsync(sb.ToString());
}

async Task ProcessComments(Post post)
{
    Console.Write("    Retrieving comments");
    var comments = await wordPressClient.Comments.GetAllCommentsForPostAsync(post.Id, true, false); // as Comment[];

    Console.WriteLine($": {comments?.Count()}");

    if (comments?.Count() == 0)
        return;

    await githubComments.ProcessPost(post, comments!);
}

async Task DownloadFile(string imageOnDisk, Uri imageUri)
{
    if (imageUri.ToString().Contains("sphear.demon.nl"))
        return;

    var path = Path.GetDirectoryName(imageOnDisk);
    if (!Directory.Exists(path)) Directory.CreateDirectory(path);

    if (string.IsNullOrEmpty(Path.GetFileName(imageOnDisk)))
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  Not a valid filename {imageOnDisk}");
        Console.ResetColor();
        return;
    }

    if (!File.Exists(imageOnDisk))
    {
        try
        {
            using var httpClient = new HttpClient();

            using var request = new HttpRequestMessage(HttpMethod.Head, imageUri);
            using var response = await httpClient.SendAsync(request);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  Could not download {imageUri}");
                Console.ResetColor();
                return;
            }

            var fileBytes = await httpClient.GetByteArrayAsync(imageUri);
            File.WriteAllBytes(imageOnDisk, fileBytes);
        }
        catch (HttpRequestException e)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Unable to download {imageUri} or store it to {imageOnDisk}");
            Console.ResetColor();

            if (!e.Message.Contains("No such host is known"))
                throw;
        }
        catch (Exception e)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Unable to download {imageUri} or store it to {imageOnDisk}");
            Console.ResetColor();
            throw;
        }
    }
}
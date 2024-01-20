using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Html2Markdown;
using Octokit.GraphQL;
using Octokit.GraphQL.Model;
using WordPressPCL.Models;
using Environment = System.Environment;

namespace WordpressToJekyll;

public class GithubComments
{
    readonly string GitHub_MyUser_Token = Environment.GetEnvironmentVariable("WeblogConverter_GitHub_MyUser");
    readonly string GitHub_Disgus_Token = Environment.GetEnvironmentVariable("WeblogConverter_GitHub_DisgusBot");
    Connection mainConnection;
    Connection botConnection;

    ID repoId;
    ID categoryId;
    List<DiscussionSummary> gitHubDiscussions;
    Converter markdownConverter;

    const string RepoOwner = "dvdstelt";
    const string RepoName = "blogcomments";
    const string Repo = $"{RepoOwner}/{RepoName}";

    public GithubComments(Converter markdownConverter)
    {
        this.markdownConverter = markdownConverter;
    }

    public async Task Initialize()
    {
        if (GitHub_Disgus_Token == null || GitHub_MyUser_Token == null)
            throw new Exception("Sorry, GitHub tokens need to be present");

        mainConnection = new Connection(new ProductHeaderValue("WeblogConverter", "1.0.0"), GitHub_MyUser_Token);
        botConnection = new Connection(new ProductHeaderValue("WeblogConverter", "1.0.0"), GitHub_Disgus_Token);

        repoId = await GetRepositoryId(mainConnection);
        categoryId = await GetAnnouncementCategoryId(mainConnection);

        gitHubDiscussions = await GetAllDiscussions(mainConnection, categoryId);
        Console.WriteLine($"Fetched {gitHubDiscussions.Count} discussions");
    }

    public async Task ProcessPost(Post post, IEnumerable<Comment> comments)
    {
        await PrintRateLimit(mainConnection, "dvdstelt");
        await PrintRateLimit(botConnection, "disgussed");

        var title = post.Date.ToString("yyyy/MM/dd") + "/" + post.Slug + "/";
        var bytes = Encoding.ASCII.GetBytes(title);
        var hashData = SHA1.HashData(bytes);
        var hash = Convert.ToHexString(hashData).ToLowerInvariant();

        var discussion = gitHubDiscussions.SingleOrDefault(s => s.Body.Contains(hash));
        if (discussion is not null)
            return;

        discussion = await CreateDiscussion(mainConnection, post, repoId, categoryId, hash);

        foreach (var comment in comments.OrderBy(s => s.Date))
        {
            await Task.Delay(2000);
            await CreateDiscussionComment(discussion, comment);
        }
    }

    private async Task CreateDiscussionComment(DiscussionSummary discussion, Comment comment)
    {
        // post comments _by_ me using my account, use bot account for others
        var connection = comment.AuthorName == "Dennis van der Stelt"
            ? mainConnection
            : botConnection;

        // Remove @dennis and other references, as those can now be actual GitHub users.
        // We don't want to ping those! :-)
        var finalComment = Regex.Replace(comment.Content.Rendered, @"@(\w+)", "At $1");

        var body = $"""
                    <em>{comment.AuthorName} commented at {comment.Date:MMMM dd yyyy, hh:mm}</em>

                    ---

                    {markdownConverter.Convert(finalComment)}
                    """;

        var mutation = new Mutation()
            .AddDiscussionComment(new AddDiscussionCommentInput
            {
                Body = body,
                DiscussionId = discussion.ID,
                ReplyToId = null,
            })
            .Select(x => new
            {
                x.Comment.Id,
                x.Comment.Url
            });

        var newComment = await connection.Run(mutation);
        if (newComment is not { })
        {
            throw new Exception($"Failed to create comment by {comment.AuthorName} for {discussion.Title}");
        }

        // return new DiscussionComment(newComment.Id, newComment.Url);
    }

    async Task<DiscussionSummary> CreateDiscussion(
        Connection connection,
        Post post,
        ID repoId,
        ID categoryId,
        string hash)
    {
        try
        {
            var title = post.Date.ToString("yyyy/MM/dd") + "/" + post.Slug + "/";
            var body = $"""
                        # {title}

                        {markdownConverter.Convert(post.Excerpt.Rendered)}

                        https://bloggingabout.net/{title}

                        <!-- sha1: {hash} -->
                        """;

            var mutation = new Mutation()
                .CreateDiscussion(new CreateDiscussionInput
                {
                    Title = title,
                    RepositoryId = repoId,
                    CategoryId = categoryId,
                    Body = body,
                })
                .Select(x => new
                {
                    x.Discussion.Title,
                    x.Discussion.Body,
                    x.Discussion.Id,
                    x.Discussion.Number,
                });

            var discussion = await connection.Run(mutation);
            if (discussion is not { })
            {
                throw new Exception($"Failed to create discussion for {title}");
            }

            Console.WriteLine($"Created discussion for post {title}");
            // adding a brief pause to avoid hitting abuse rate-limits
            // https://github.com/cli/cli/issues/4801
            await Task.Delay(3_000);
            return new DiscussionSummary(
                discussion.Title,
                discussion.Body,
                discussion.Id,
                discussion.Number);
        }
        catch (Exception)
        {
            Console.WriteLine($"Error creating discussion for post {post.Title.Rendered}");
            throw;
        }
    }

    async Task<List<DiscussionSummary>> GetAllDiscussions(Connection connection, ID categoryId)
    {
        try
        {
            var results = new Dictionary<string, DiscussionSummary>();
            string? cursor = null;
            var orderBy = new DiscussionOrder()
                { Direction = OrderDirection.Asc, Field = DiscussionOrderField.CreatedAt };
            while (true)
            {
                var query = new Query()
                    .Repository(name: RepoName, owner: RepoOwner)
                    .Discussions(first: 100, after: cursor, categoryId: categoryId, orderBy: orderBy)
                    .Edges.Select(e => new
                    {
                        e.Cursor,
                        e.Node.Title,
                        e.Node.Body,
                        e.Node.Id,
                        e.Node.Number,
                    })
                    .Compile();

                var result = (await connection.Run(query)).ToList();
                if (!result.Any())
                {
                    return results.Values.ToList();
                }

                cursor = result.Last().Cursor;
                foreach (var discussion in result)
                {
                    results.TryAdd(discussion.Id.Value, new DiscussionSummary(
                        discussion.Title,
                        discussion.Body,
                        discussion.Id,
                        discussion.Number));
                }
            }
        }
        catch (Exception)
        {
            Console.WriteLine($"Error fetching discussions");
            throw;
        }
    }

    async Task<ID> GetRepositoryId(Connection connection)
    {
        try
        {
            var query = new Query()
                .Repository(name: RepoName, owner: RepoOwner)
                .Select(x => x.Id)
                .Compile();

            return await connection.Run(query);
        }
        catch (Exception)
        {
            Console.WriteLine("Error fetching repo ID");
            throw;
        }
    }

    async Task<ID> GetAnnouncementCategoryId(Connection connection)
    {
        try
        {
            var query = new Query()
                .Repository(name: RepoName, owner: RepoOwner)
                .DiscussionCategories(first: 10)
                .Nodes
                .Select(x => new
                {
                    x.Id,
                    x.Name,
                })
                .Compile();

            var result = await connection.Run(query);
            return result.Single(x => x.Name == "General").Id;
        }
        catch (Exception)
        {
            Console.WriteLine("Error fetching repo ID");
            throw;
        }
    }


    async Task PrintRateLimit(Connection connection, string connectionName)
    {
        try
        {
            var query = new Query()
                .RateLimit()
                .Select(x => new
                {
                    x.Limit,
                    x.Remaining,
                    x.ResetAt
                })
                .Compile();

            var results = await connection.Run(query);
            Console.WriteLine(
                $"The {connectionName} connection currently has {results.Remaining} of {results.Limit}. Resets at {results.ResetAt:T} ");
        }
        catch (Exception)
        {
            Console.WriteLine("[red]Error fetching rate limit[/]");
        }
    }
}

record DiscussionSummary(string Title, string Body, ID ID, int Number);
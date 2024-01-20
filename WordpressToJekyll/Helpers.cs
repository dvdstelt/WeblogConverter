namespace WordpressToJekyll;

public static class Helpers
{
    public static string PrepareForMarkdown(this string value)
    {
        value = value.Replace("\"", "");
        value = value.Replace("/ ", "");
        value = value.Replace("/", "");
        value = value.Replace(":", "");
        value = value.Replace("-", "");
        value = value.Replace("[spam] ", "");
        value = value.Replace("[Spam] ", "");
        value = value.Replace("[offtopic] ", "");
        value = value.Replace("[VB] ", "");
        value = value.Replace("[winform] ", "");
        value = value.Replace("&quot;", "");
        value = value.Replace("&amp;", "");
        return value;
    }
}
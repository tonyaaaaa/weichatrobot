namespace WechatRobot.Api;

internal static class Pagination
{
    private const int MaximumPage = 1_000_000;

    public static bool TryNormalize(int requestedPage, int requestedPageSize, out int page, out int pageSize, out int skip)
    {
        page = Math.Max(1, requestedPage);
        pageSize = Math.Clamp(requestedPageSize <= 0 ? 20 : requestedPageSize, 1, 100);
        var longSkip = ((long)page - 1) * pageSize;
        if (page > MaximumPage || longSkip > int.MaxValue)
        {
            skip = 0;
            return false;
        }
        skip = (int)longSkip;
        return true;
    }
}

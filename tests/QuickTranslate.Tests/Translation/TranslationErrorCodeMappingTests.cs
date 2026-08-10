using QuickTranslate.Core.Translation;
using Xunit;

namespace QuickTranslate.Tests.Translation;

public class TranslationErrorCodeMappingTests
{
    [Fact]
    public void Case_401_Unauthorized_Returns_AuthFailed()
    {
        var ex = TranslationException.FromHttpStatus(401, "Unauthorized body");
        Assert.Equal(TranslationErrorCode.AuthFailed, ex.ErrorCode);
        Assert.Contains("Unauthorized", ex.Message);
    }

    [Fact]
    public void Case_403_Forbidden_Returns_AuthFailed()
    {
        var ex = TranslationException.FromHttpStatus(403, "Forbidden body");
        Assert.Equal(TranslationErrorCode.AuthFailed, ex.ErrorCode);
        Assert.Contains("Forbidden", ex.Message);
    }

    [Fact]
    public void Case_500_ServerError_Returns_Unknown()
    {
        var ex = TranslationException.FromHttpStatus(500);
        Assert.Equal(TranslationErrorCode.Unknown, ex.ErrorCode);
        Assert.Contains("服务端错误", ex.Message);
        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public void Case_429_TooManyRequests_Returns_Throttled()
    {
        var ex = TranslationException.FromHttpStatus(429, "Rate limited");
        Assert.Equal(TranslationErrorCode.Throttled, ex.ErrorCode);
        Assert.Contains("Rate limited", ex.Message);
    }
}

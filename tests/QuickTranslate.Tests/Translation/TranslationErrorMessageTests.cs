using QuickTranslate.Core.Translation;
using Xunit;

namespace QuickTranslate.Tests.Translation;

public class TranslationErrorMessageTests
{
    [Fact]
    public void ForCode_Timeout_ContainsTimeout()
    {
        var msg = TranslationErrorMessage.ForCode(TranslationErrorCode.Timeout);
        Assert.Contains("超时", msg);
        AssertNoStackLeak(msg);
    }

    [Fact]
    public void ForCode_AuthFailed_ContainsAuth()
    {
        var msg = TranslationErrorMessage.ForCode(TranslationErrorCode.AuthFailed);
        Assert.Contains("授权", msg);
        AssertNoStackLeak(msg);
    }

    [Fact]
    public void ForCode_NetworkUnavailable_ContainsNoNetwork()
    {
        var msg = TranslationErrorMessage.ForCode(TranslationErrorCode.NetworkUnavailable);
        Assert.Contains("无网络", msg);
        AssertNoStackLeak(msg);
    }

    [Fact]
    public void ForCode_InvalidResponse_ContainsResponseError()
    {
        var msg = TranslationErrorMessage.ForCode(TranslationErrorCode.InvalidResponse);
        Assert.Contains("响应异常", msg);
        AssertNoStackLeak(msg);
    }

    [Fact]
    public void ForCode_Throttled_ContainsFrequent()
    {
        var msg = TranslationErrorMessage.ForCode(TranslationErrorCode.Throttled);
        Assert.Contains("频繁", msg);
        AssertNoStackLeak(msg);
    }

    [Fact]
    public void ForCode_BadRequest_ContainsParam()
    {
        var msg = TranslationErrorMessage.ForCode(TranslationErrorCode.BadRequest);
        Assert.Contains("参数", msg);
        AssertNoStackLeak(msg);
    }

    [Fact]
    public void ForCode_Unknown_ContainsFailed()
    {
        var msg = TranslationErrorMessage.ForCode(TranslationErrorCode.Unknown);
        Assert.Contains("失败", msg);
        AssertNoStackLeak(msg);
    }

    private static void AssertNoStackLeak(string msg)
    {
        Assert.DoesNotContain("Exception", msg, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", msg, StringComparison.Ordinal);
        Assert.DoesNotContain("at ", msg, StringComparison.Ordinal);
    }
}

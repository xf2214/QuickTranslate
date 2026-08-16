using QuickTranslate.App.Validation;
using Xunit;

namespace QuickTranslate.Tests.App;

public class CustomLlmValidationTests
{
    [Fact]
    public void AllowEmpty_True_PassesWithEmptyConfig()
    {
        var r = SettingsValidator.ValidateCustomLlm("", " ", allowEmpty: true);
        Assert.True(r.IsSuccess);
    }

    [Fact]
    public void AllowEmpty_False_FailsWithEmptyConfig()
    {
        var r = SettingsValidator.ValidateCustomLlm("", "", allowEmpty: false);
        Assert.False(r.IsSuccess);
        Assert.Contains("API 地址", r.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("api.openai.com/v1")]           // 缺 scheme
    [InlineData("ftp://api.openai.com/v1")]     // 非 http(s)
    [InlineData("http://a b.com")]              // 非法 URL
    public void InvalidBaseUrl_Fails(string url)
    {
        var r = SettingsValidator.ValidateCustomLlm(url, "gpt-4o-mini", allowEmpty: false);
        Assert.False(r.IsSuccess);
    }

    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://api.openai.com/v1/")]
    [InlineData("http://127.0.0.1:11434/v1")]
    [InlineData("https://api.deepseek.com")]
    public void ValidBaseUrl_Passes(string url)
    {
        var r = SettingsValidator.ValidateCustomLlm(url, "gpt-4o-mini", allowEmpty: false);
        Assert.True(r.IsSuccess);
    }

    [Fact]
    public void MissingModel_Fails()
    {
        var r = SettingsValidator.ValidateCustomLlm("https://api.openai.com/v1", "", allowEmpty: false);
        Assert.False(r.IsSuccess);
        Assert.Contains("模型名称", r.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void PartialFill_Fails_EvenIfAllowEmpty()
    {
        // 填了一半（有 URL 无模型）必须报错，避免用户以为已配置成功
        var r = SettingsValidator.ValidateCustomLlm("https://api.openai.com/v1", "", allowEmpty: true);
        Assert.False(r.IsSuccess);
    }
}

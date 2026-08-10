namespace QuickTranslate.Core.Common;

public readonly record struct Result(bool IsSuccess, string? ErrorMessage = null)
{
    public static Result Ok() => new(true, null);
    public static Result Fail(string msg) => new(false, msg);
}

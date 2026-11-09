using System.Data;
using System.Globalization;
using Dapper;

namespace FileStash.Infrastructure.Data;

/// <summary>
/// DateTimeOffset ↔ UTC ISO-8601 字符串。
/// </summary>
/// <remarks>
/// 时间存储为 UTC ISO-8601 字符串，显示时转本地时间。
/// Dapper 默认会把字符串转 DateTimeOffset 失败，所以自己写一个处理器。
/// </remarks>
public sealed class Iso8601DateTimeOffsetTypeHandler : SqlMapper.TypeHandler<DateTimeOffset>
{
    private const string Format = "O";

    public override DateTimeOffset Parse(object value) =>
        DateTimeOffset.Parse(
            (string)value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    public override void SetValue(IDbDataParameter parameter, DateTimeOffset value) =>
        parameter.Value = value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture);
}

using System.Globalization;
using FileStash.App.Converters;
using FileStash.Core.Models;

namespace FileStash.Core.Tests;

public sealed class WorkbenchChildrenConverterTests
{
    [Theory]
    [InlineData(false, WorkbenchViewMode.Tree, 0)]
    [InlineData(true, WorkbenchViewMode.Tree, 1)]
    [InlineData(true, WorkbenchViewMode.List, 0)]
    public void Children_ShouldShowFolderContentsWithoutRepeatingSingleFiles(
        bool isFolder, WorkbenchViewMode mode, int expectedCount)
    {
        var children = new object[] { new() };
        var converter = new WorkbenchChildrenConverter();
        var result = converter.Convert([children, mode, isFolder], typeof(object), null, CultureInfo.InvariantCulture);
        Assert.Equal(expectedCount, Assert.IsAssignableFrom<IEnumerable<object>>(result).Count());
    }
}

namespace VbaDev.App.Build;

internal sealed class WorkbookMaterializationOutputValidator
{
    public void Validate(string stagingWorkbookPath)
    {
        try
        {
            using var stream = new FileStream(
                stagingWorkbookPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            if (stream.Length == 0)
            {
                throw new BuildCommandException(
                    $"The saved staging workbook is empty: {stagingWorkbookPath}");
            }
        }
        catch (BuildCommandException)
        {
            throw;
        }
        catch (IOException ex)
        {
            throw new BuildCommandException(
                $"The saved staging workbook could not be read: {stagingWorkbookPath}",
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new BuildCommandException(
                $"The saved staging workbook could not be read: {stagingWorkbookPath}",
                ex);
        }
    }
}

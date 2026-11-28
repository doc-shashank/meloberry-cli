using Meloberry.Domain.Analysis;
using Meloberry.Domain.Common;
using Meloberry.Domain.Layouts;
using Meloberry.Domain.Questions;
using Meloberry.Domain.Quizzes;
using Meloberry.Domain.Sessions;

namespace Meloberry.Application.Abstractions;

public enum ContentInputMethod { String, Path }
public enum ResolvedContentKind { Text, Image }

public sealed record ResolvedContent(ResolvedContentKind Kind, string? TextContent, string? RelativeAssetPath);
public sealed record JsonMcqOptionSpec(int Index, string? Text, string? ImagePath, bool IsCorrect);
public sealed record JsonFlashcardSideSpec(string? Text, string? ImagePath, string? TextPosition);

public sealed record JsonQuestionSpec(
    string Type,
    string? Text,
    int? MaxOptions,
    bool IsMultiCorrect,
    IReadOnlyList<JsonMcqOptionSpec> Options,
    JsonFlashcardSideSpec? Front,
    JsonFlashcardSideSpec? Back,
    Dictionary<string, string>? LayoutOverrides,
    string? ImagePath = null,
    string? Explanation = null,
    string? Ref = null,
    string? PayloadJson = null,
    IReadOnlyList<JsonQuestionSpec>? SubQuestions = null);

public sealed record DeclarativeArticleBlockSpec(
    string Kind,
    string? Text,
    string? ImagePath,
    string? Ref = null,
    string? Caption = null,
    string? Alt = null,
    string? Tone = null,
    string? Title = null,
    string? PayloadJson = null);

public sealed record DeclarativeLinkCardSpec(
    string Id,
    string? Title,
    string? Text,
    string? ImagePath = null);

public sealed record DeclarativeComponentSpec(
    string Kind,
    string? Title,
    string? QuizType,
    IReadOnlyList<DeclarativeArticleBlockSpec>? Blocks,
    IReadOnlyList<JsonQuestionSpec>? Questions,
    Dictionary<string, string>? LayoutOverrides,
    string? Ref = null,
    string? SourceDirectory = null,
    IReadOnlyList<DeclarativeLinkCardSpec>? Links = null);

public sealed record DeclarativeApplySpec(
    string? Kind,
    string? Name,
    string? Description,
    string? Title,
    string? QuizType,
    IReadOnlyList<DeclarativeComponentSpec>? Components,
    IReadOnlyList<DeclarativeArticleBlockSpec>? Blocks,
    IReadOnlyList<JsonQuestionSpec>? Questions,
    Dictionary<string, string>? LayoutOverrides,
    string? AssetBaseDirectory = null,
    string? Ref = null,
    IReadOnlyList<string>? Order = null,
    IReadOnlyList<DeclarativeLinkCardSpec>? Links = null);

public interface IDeclarativeSpecLoader
{
    Task<Result<DeclarativeApplySpec>> LoadAsync(string specPath, CancellationToken cancellationToken = default);
    Task<Result<DeclarativeApplySpec>> LoadFromTextAsync(string rawText, CancellationToken cancellationToken = default);
}

public sealed record SessionLookupContext(StudySession Session, string StorageDirectory);
public sealed record ComponentLookupContext(StudySession Session, SessionComponent Component, string StorageDirectory);
public sealed record ArticleBlockLookupContext(StudySession Session, SessionComponent Component, ArticleBlock Block, string StorageDirectory);
public sealed record QuizOwnerContext(StudySession? Session, SessionComponent? Component, Quiz Quiz, string StorageDirectory);

public sealed record QuestionOwnerContext(StudySession? Session, SessionComponent? Component, Quiz Quiz, Question Question, string StorageDirectory)
{
    public QuizOwnerContext ToQuizOwner() => new(Session, Component, Quiz, StorageDirectory);
}

public sealed record WorkspaceListItem(string Id, string Kind, string SubType, string StorageDirectory, int ItemCount);
public sealed record PackageImportResult(WorkspaceListItem Workspace, string TargetPlatform = "desktop", bool IncludedLayout = false);

public static class PathConfinement
{
    public static bool TryResolveInside(string rootDirectory, string? relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(rootDirectory) || string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            return false;

        var rootFull = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootPrefix = rootFull + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(rootFull, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        fullPath = candidate;
        return true;
    }

    public static bool TryResolveConfinedInputFile(
        string rawPath,
        string componentBaseDir,
        string? specRootDir,
        string? cwdDir,
        out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(rawPath)) return false;

        if (TryResolveInside(componentBaseDir, rawPath, out var compCandidate))
            resolvedPath = compCandidate;
        else if (!string.IsNullOrWhiteSpace(specRootDir) && TryResolveRelativeInsideRoot(componentBaseDir, rawPath, specRootDir, out var nestedCandidate))
            resolvedPath = nestedCandidate;
        else if (!string.IsNullOrWhiteSpace(specRootDir) && TryResolveInside(specRootDir, rawPath, out var rootCandidate))
            resolvedPath = rootCandidate;
        else
            return false;

        if (!File.Exists(resolvedPath) && !StartsWithParentTraversal(rawPath))
        {
            if (!string.IsNullOrWhiteSpace(specRootDir) && TryResolveInside(specRootDir, rawPath, out var rootFallback) && File.Exists(rootFallback))
                resolvedPath = rootFallback;
            else if (!string.IsNullOrWhiteSpace(cwdDir) && TryResolveInside(cwdDir, rawPath, out var cwdFallback) && File.Exists(cwdFallback))
                resolvedPath = cwdFallback;
        }

        return true;
    }

    private static bool TryResolveRelativeInsideRoot(string baseDir, string rawPath, string rootDir, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(rawPath) || rawPath.Contains('\0')) return false;
        var trimmed = rawPath.Trim();
        if (Path.IsPathRooted(trimmed)) return false;
        try
        {
            var rootFull = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidateFull = Path.GetFullPath(Path.Combine(baseDir, trimmed));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (candidateFull.StartsWith(rootFull + Path.DirectorySeparatorChar, comparison))
            {
                resolvedPath = candidateFull;
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool StartsWithParentTraversal(string rawPath)
    {
        var normalized = rawPath.Trim().Replace('/', '\\');
        return normalized.Equals("..", StringComparison.Ordinal) ||
               normalized.StartsWith("..\\", StringComparison.Ordinal);
    }
}

public interface IWorkingDirectoryProvider
{
    string GetCurrentDirectory();
    string NormalizeDirectoryPath(string? explicitPath);
    bool DirectoryExists(string path);
    bool FileExists(string path);
}

public interface IIdGenerator
{
    string NewSessionId();
    string NewComponentId();
    string NewArticleBlockId();
    string NewQuizId();
    string NewQuestionId();
}

public interface IContentInspector
{
    Task<Result<ResolvedContent>> ResolveInputAsync(
        ContentInputMethod method, string valueOrPath, string workspaceStorageDirectory,
        bool requireImageOnly = false, CancellationToken cancellationToken = default);

    Task<Result<string>> CopyAndVerifyImageAsync(
        string sourceImagePath, string workspaceStorageDirectory, CancellationToken cancellationToken = default);

    Task<Result> VerifyStoredAssetAsync(
        string workspaceStorageDirectory, string relativeAssetPath, CancellationToken cancellationToken = default);
}

public interface IWorkspaceRepository
{
    Task CreateSessionAsync(StudySession session, CancellationToken cancellationToken = default);
    Task SaveSessionAsync(StudySession session, CancellationToken cancellationToken = default);
    Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task CreateQuizAsync(Quiz quiz, CancellationToken cancellationToken = default);
    Task SaveQuizAsync(Quiz quiz, CancellationToken cancellationToken = default);
    Task DeleteQuizAsync(string quizId, CancellationToken cancellationToken = default);
    Task<SessionLookupContext?> FindSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<ComponentLookupContext?> FindComponentAsync(string componentId, CancellationToken cancellationToken = default);
    Task<ArticleBlockLookupContext?> FindArticleBlockAsync(string artCompId, CancellationToken cancellationToken = default);
    Task<QuizOwnerContext?> FindQuizContainerAsync(string quizOrComponentId, CancellationToken cancellationToken = default);
    Task<QuestionOwnerContext?> FindQuestionOwnerAsync(string questionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkspaceListItem>> ListWorkspacesAsync(string? directoryFilter = null, bool includeAll = false, CancellationToken cancellationToken = default);
    Task SaveOwnerAsync(QuizOwnerContext owner, CancellationToken cancellationToken = default);

    Task UpdateSessionMetadataAsync(StudySession session, CancellationToken cancellationToken = default) =>
        SaveSessionAsync(session, cancellationToken);

    Task UpdateQuizMetadataAsync(Quiz quiz, CancellationToken cancellationToken = default) =>
        SaveQuizAsync(quiz, cancellationToken);

    Task UpdateArticleBlockAsync(StudySession session, ArticleBlock block, CancellationToken cancellationToken = default) =>
        SaveSessionAsync(session, cancellationToken);

    Task UpdateQuestionAsync(QuizOwnerContext owner, Question question, CancellationToken cancellationToken = default) =>
        SaveOwnerAsync(owner, cancellationToken);

    Task UpdateComponentAsync(StudySession session, SessionComponent component, CancellationToken cancellationToken = default) =>
        SaveSessionAsync(session, cancellationToken);
}

public interface ILayoutRepository
{
    Task<LayoutProfile> GetProfileAsync(string targetType, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, LayoutProfile>> GetAllProfilesAsync(CancellationToken cancellationToken = default);
    Task SaveProfileAsync(LayoutProfile profile, CancellationToken cancellationToken = default);
    Task SaveProfilesAsync(IReadOnlyCollection<LayoutProfile> profiles, CancellationToken cancellationToken = default);
}

public interface IPackageExporter
{
    Task<Result<string>> ExportSessionAsync(
        StudySession session, string storageDirectory, IReadOnlyDictionary<string, LayoutProfile> layouts,
        string? outputPath, string targetPlatform = "desktop", bool includeLayouts = false, CancellationToken cancellationToken = default);

    Task<Result<string>> ExportQuizAsync(
        Quiz quiz, string storageDirectory, IReadOnlyDictionary<string, LayoutProfile> layouts,
        string? outputPath, string targetPlatform = "desktop", bool includeLayouts = false, CancellationToken cancellationToken = default);

    Task<Result<PackageImportResult>> ImportPackageAsync(
        string packagePath, string destinationDirectory, CancellationToken cancellationToken = default);
}

public interface IWebAppGenerator
{
    Task<Result<string>> GenerateForSessionAsync(
        StudySession session, string storageDirectory, IReadOnlyDictionary<string, LayoutProfile> layouts,
        string? outputDirectory, CancellationToken cancellationToken = default);

    Task<Result<string>> GenerateForQuizAsync(
        Quiz quiz, string storageDirectory, IReadOnlyDictionary<string, LayoutProfile> layouts,
        string? outputDirectory, CancellationToken cancellationToken = default);
}

public sealed record SavedAnalysisFiles(string JsonPath, string MarkdownPath, SessionAnalysisReport Report);

public sealed record WebAppVerificationReport(
    bool Passed,
    string WebDir,
    string Engine,
    int SlidesChecked,
    int ImagesChecked,
    IReadOnlyList<string> ConsoleErrors,
    IReadOnlyList<string> BrokenAssets,
    IReadOnlyList<string> OverflowIssues,
    IReadOnlyList<string> KatexErrors,
    string? ReportPath);

public interface IHeadlessWebAppVerifier
{
    Task<Result<WebAppVerificationReport>> VerifyAsync(
        string webDirectory,
        bool staticOnly = false,
        int timeoutSeconds = 30,
        CancellationToken cancellationToken = default);
}

public interface IAnalysisReportStore
{
    Task<Result<IReadOnlyList<SessionAnalysisReport>>> LoadReportsAsync(string webAppOrReportPath, CancellationToken cancellationToken = default);
    Task<Result<SavedAnalysisFiles>> SaveReportToWebAppDirectoryAsync(string webAppDirectory, string rawReportJson, string? rawMarkdown = null, CancellationToken cancellationToken = default);
}

public static class CliCapabilities
{
    public static readonly IReadOnlyList<string> All =
    [
        "get.view.spec",
        "get.path.dir",
        "patch.component.links",
        "patch.case.declarative",
        "spec.icons",
        "spec.nested",
        "pkg.import.metadata",
        "batch.atomic"
    ];
}

public sealed record CliToolMetadata(
    string ProductName,
    string ExecutableName,
    string Version,
    string Runtime,
    string AppName = "Meloberry",
    string RepositoryUrl = "https://github.com/doc-shashank/meloberry-cli/",
    IReadOnlyList<string>? Capabilities = null);

public interface ICliToolMetadataProvider { CliToolMetadata GetMetadata(); }
public interface ILayoutEditorPageBuilder { string BuildHtml(string initialComponent, CliToolMetadata toolMetadata, string? csrfToken = null); }
public interface ISpecIconProvider { IReadOnlyDictionary<string, string> GetIcons(IEnumerable<string>? topics = null); }

public interface IChainTransactionScope : IDisposable
{
    void Commit();
    void Rollback();
}

public interface IChainTransactionManager
{
    IChainTransactionScope Begin();
}

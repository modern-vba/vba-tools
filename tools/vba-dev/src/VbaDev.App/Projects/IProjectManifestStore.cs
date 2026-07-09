using VbaDev.Domain;

namespace VbaDev.App.Projects;

public interface IProjectManifestStore
{
    ProjectManifest Load(string manifestPath);

    void Save(string projectRoot, ProjectManifest manifest);
}

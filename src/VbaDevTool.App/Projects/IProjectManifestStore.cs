using VbaDevTools.Domain;

namespace VbaDevTools.App.Projects;

public interface IProjectManifestStore
{
    ProjectManifest Load(string manifestPath);

    void Save(string projectRoot, ProjectManifest manifest);
}

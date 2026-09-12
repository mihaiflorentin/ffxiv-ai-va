namespace AIVoiceActing;

using Dalamud.Configuration;
/// <summary>
/// Dalamud persistence boundary: adds the <see cref="IPluginConfiguration"/> marker (satisfied
/// by <see cref="Configuration.Version"/>) so the option surface itself stays Dalamud-free
/// and testable on any OS. Loaded/saved through IDalamudPluginInterface; System.Text.Json
/// deserializes stored JSON over a fresh instance, so fields missing from older files keep
/// their defaults.
/// </summary>
public sealed class PluginConfiguration : Configuration, IPluginConfiguration
{
}

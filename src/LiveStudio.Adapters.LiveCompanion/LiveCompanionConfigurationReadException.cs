namespace LiveStudio.Adapters.LiveCompanion;

// Contains only known relative paths and fixed diagnostic messages, never native JSON values.
internal sealed class LiveCompanionConfigurationReadException(string message) : IOException(message);

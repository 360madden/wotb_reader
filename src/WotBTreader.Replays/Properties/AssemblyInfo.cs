using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("WotBTreader.Replays.Tests")]

// Synthetic fixtures are built against the real format constants so a fixture
// can never drift from the decoder it exercises.
[assembly: InternalsVisibleTo("WotBTreader.TestSupport")]

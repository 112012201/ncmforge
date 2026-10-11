using System.Runtime.CompilerServices;

// The first-party command-line front end and the test suite exercise container internals
// (layout scanning, keystream phase) directly, which is deliberately not part of the public API.
[assembly: InternalsVisibleTo("ncmforge")]
[assembly: InternalsVisibleTo("NcmForge.Tests")]

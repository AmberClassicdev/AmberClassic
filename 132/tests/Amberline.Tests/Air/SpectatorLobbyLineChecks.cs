using System.Text;
using Amberline.Network.Spectator;
using Amberline.Platform;
using Amberline.Tests.Network;
using RtmpSharp.IO;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 848: what the 1.0.0.132 line's AIR 1.52 reads for spectating. Its SpectatorController.startSpectating hands Maestro the new
/// <c>observerEncryptionKey</c> (SpectatorController.as:219-239), so <c>encryptionKey</c> stays empty for a spectator; and every answer and
/// push of the shared lobby scenario (<see cref="AirSpectatorLobbyChecks"/>), the spectator's start included, names only fields its 1.52 class
/// declares (air152-declared-fields.json). The system states' observerModeEnabled and spectatorSlotLimit are pinned by
/// <c>server.air132-system-states-externalizable-json</c>.
/// </summary>
public static class SpectatorLobbyLineChecks
{


}

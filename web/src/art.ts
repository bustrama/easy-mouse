// Album art as a data: URL. The server's CSP is `img-src 'self' data:` (docs/protocol.md), so a blob: object URL
// would be blocked and widening the CSP is not worth it for a picture of at most 128 KiB. A data: URL is a plain
// string: nothing to revoke, and replacing the state value releases the old image.

export interface Art {
  id: string;
  url: string;
}

function toBase64(data: Uint8Array): string {
  let bin = '';
  for (let i = 0; i < data.length; i += 0x8000) bin += String.fromCharCode(...data.subarray(i, i + 0x8000));
  return btoa(bin);
}

export function makeArt(id: string, type: 'jpeg' | 'png', data: Uint8Array): Art {
  return { id, url: `data:image/${type};base64,${toBase64(data)}` };
}

/** The image to show: only the one that belongs to the current state's `artId`. */
export const coverFor = (art: Art | null, artId: string | null): string | null => (art && artId !== null && art.id === artId ? art.url : null);

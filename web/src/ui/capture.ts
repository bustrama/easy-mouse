/** Keep receiving a pointer's events outside the element. Can throw for a pointer that is already gone. */
export function capture(e: PointerEvent): void {
  try {
    (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId);
  } catch {
    /* the pointer ended already; nothing to capture */
  }
}

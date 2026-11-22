export function pairErrorMessage(status: number): string {
  switch (status) {
    case 403:
      return 'Wrong code';
    case 404:
      return 'Pairing is not open on the PC. Start Add Device in the editor.';
    case 410:
      return 'This code expired or was blocked. Show a new code on the PC.';
    default:
      return 'Try again';
  }
}

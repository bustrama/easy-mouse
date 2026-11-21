import { render } from 'preact';
import { App } from './ui/App.tsx';
import { client } from './client.ts';
import './styles.css';

// Reads and strips #k= from the address bar before anything renders.
client.start();

// iOS ignores user-scalable=no: block pinch and double-tap zoom explicitly.
for (const type of ['gesturestart', 'gesturechange', 'gestureend']) {
  document.addEventListener(type, (e) => e.preventDefault(), { passive: false });
}
document.addEventListener(
  'touchmove',
  (e) => {
    if (e.touches.length > 1) e.preventDefault();
  },
  { passive: false },
);

render(<App />, document.getElementById('app')!);

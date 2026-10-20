import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import { device } from './lib/device';
import './styles.css';

// Разметка выбирается атрибутом, а не медиазапросами ширины (разбор в lib/device.ts).
document.documentElement.dataset.device = device;

createRoot(document.getElementById('root') as HTMLElement).render(
  <StrictMode>
    <App />
  </StrictMode>,
);

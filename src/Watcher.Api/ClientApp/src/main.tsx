import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import '@fontsource-variable/inter/wght.css';
import App from './App';
import './styles/app.css';

const container = document.getElementById('root');
if (!container) throw new Error('Не знайдено контейнер інтерфейсу.');

createRoot(container).render(
  <StrictMode>
    <App />
  </StrictMode>,
);

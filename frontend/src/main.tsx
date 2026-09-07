import React from 'react';
import ReactDOM from 'react-dom/client';
import 'antd/dist/reset.css';
import { App } from './App';

// Theme and the Ant App context live inside App, because both depend on the light/dark choice
// it holds. Putting a ConfigProvider here too would give the app two sources of truth for its
// palette, and the one further from the state would quietly win.
ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>,
);

import { Routes } from '@angular/router';

const chat = () => import('./chat/chat-page').then((m) => m.ChatPage);

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'chat' },
  { path: 'chat', title: 'Chat - Notebook Assistant', loadComponent: chat },
  { path: 'chat/:id', title: 'Chat - Notebook Assistant', loadComponent: chat },
  {
    path: 'documents',
    title: 'Documents - Notebook Assistant',
    loadComponent: () => import('./documents/documents-page').then((m) => m.DocumentsPage),
  },
  { path: '**', redirectTo: 'chat' },
];

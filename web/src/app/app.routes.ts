import { Routes } from '@angular/router';

import { authGuard, guestGuard } from './auth/guards';

const chat = () => import('./chat/chat-page').then((m) => m.ChatPage);

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'chat' },
  { path: 'login', title: 'Sign in - Notebook Assistant', canActivate: [guestGuard], loadComponent: () => import('./auth/login-page').then((m) => m.LoginPage) },
  { path: 'register', title: 'Create an account - Notebook Assistant', canActivate: [guestGuard], loadComponent: () => import('./auth/register-page').then((m) => m.RegisterPage) },
  { path: 'chat', title: 'Chat - Notebook Assistant', canActivate: [authGuard], loadComponent: chat },
  { path: 'chat/:id', title: 'Chat - Notebook Assistant', canActivate: [authGuard], loadComponent: chat },
  {
    path: 'documents',
    title: 'Documents - Notebook Assistant',
    canActivate: [authGuard],
    loadComponent: () => import('./documents/documents-page').then((m) => m.DocumentsPage),
  },
  { path: '**', redirectTo: 'chat' },
];

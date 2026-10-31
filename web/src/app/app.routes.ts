import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'documents' },
  {
    path: 'documents',
    title: 'Documents - Notebook Assistant',
    loadComponent: () => import('./documents/documents-page').then((m) => m.DocumentsPage),
  },
  { path: '**', redirectTo: 'documents' },
];

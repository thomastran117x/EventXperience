import fs from 'fs';
import dotenv from 'dotenv';

dotenv.config();

const ALL_FEATURE_KEYS = [
  'auth',
  'clubs',
  'clubs.discussions',
  'clubs.follow',
  'clubs.posts',
  'clubs.reviews',
  'clubs.versioning',
  'events',
  'events.analytics',
  'events.favourites',
  'events.images',
  'events.invitations',
  'events.recentlyviewed',
  'events.registration',
  'events.versioning',
  'events.waitlist',
  'payment',
  'profile',
  'profile.admin',
  'search',
  'search.reindex',
];

function toEnvVarName(featureKey) {
  return `FEATURE_${featureKey.replace(/\./g, '_').toUpperCase()}`;
}

function parseBoolean(value, envName) {
  if (value === undefined || value === null || value === '') {
    return undefined;
  }

  if (value === 'true') {
    return true;
  }

  if (value === 'false') {
    return false;
  }

  throw new Error(`Expected ${envName} to be true or false.`);
}

const featureFlags = Object.fromEntries(
  ALL_FEATURE_KEYS.map((key) => {
    const envName = toEnvVarName(key);
    return [key, parseBoolean(process.env[envName], envName)];
  }).filter(([, value]) => value !== undefined),
);

const environment = {
  production: process.env.NODE_ENV === 'production',
  backendUrl: process.env.BACKEND_URL ?? 'http://localhost:8090/api',
  frontendUrl: process.env.FRONTEND_URL ?? 'http://localhost:3090',
  googleClientId: process.env.GOOGLE_CLIENT_ID ?? '',
  msalClientId: process.env.MSAL_CLIENT_ID ?? '',
  googleSiteKey: process.env.GOOGLE_SITE_KEY ?? '',
  featureFlags,
};

const filePath = './src/environments/environment.ts';

const content = `/**
 * Auto-generated from .env. Do not edit manually.
 */
import type { FeatureFlags } from '../app/core/features/feature-flags.types';

export const environment: {
  production: boolean;
  backendUrl: string;
  frontendUrl: string;
  googleClientId: string;
  msalClientId: string;
  googleSiteKey: string;
  featureFlags: FeatureFlags;
} = ${JSON.stringify(environment, null, 2)};
`;

fs.writeFileSync(filePath, content);
console.log('Generated src/environments/environment.ts from .env');

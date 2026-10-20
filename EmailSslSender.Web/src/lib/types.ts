// Ответы API — ровно те поля, что отдают контроллеры (snake_case, как в JSON).

export interface Tokens {
  access_token: string;
  access_expires_at: string;
}

export interface Me {
  id: string;
  email: string;
  display_name: string | null;
  must_change_password: boolean;
  must_enroll_two_factor: boolean;
}

export interface SecondFactorChallenge {
  challenge: string;
  method: 'totp' | 'email' | string;
  available?: string[];
}

export interface TwoFactorStatus {
  confirmed: boolean;
  available_methods: string[] | null;
}

export interface TotpSetup {
  secret: string;
  uri: string;
}

export interface PasswordPolicy {
  min_length: number;
  require_uppercase: boolean;
  require_lowercase: boolean;
  require_digit: boolean;
  require_symbol: boolean;
}

export type Delivery = 'none' | 'queued' | 'sent' | 'failed';

export interface Subscription {
  id: string;
  common_name: string;
  recipients: string[];
  formats: string[];
  include_passphrase: boolean;
  on_change: boolean;
  interval_days: number | null;
  locale: string;
  enabled: boolean;
  last_sent_serial: string | null;
  last_sent_at: string | null;
  last_error: string | null;
  delivery: Delivery;
}

export interface KeyManagerStatus {
  enabled: boolean;
  configured: boolean;
  api_key_set: boolean;
  last_poll_at: string | null;
  last_poll_error: string | null;
  last_poll_count: number | null;
}

export interface KnownCertificate {
  common_name: string;
  serial_number: string;
  expires_on: string | null;
  first_seen_at: string;
  last_seen_at: string;
}

export interface MailStatus {
  configured: boolean;
  password_set: boolean;
  pending: number;
  failed: number;
  last_error: string | null;
}

export interface Setting {
  key: string;
  nullable: boolean;
  value: unknown;
}

export interface User {
  id: string;
  email: string;
  display_name: string | null;
  is_active: boolean;
  activated_at: string | null;
  locked_until: string | null;
  authenticator_enabled: boolean;
}

export interface UsersPage {
  items: User[];
  next_cursor: string | null;
}

export interface UserCreated {
  invitation_token: string | null;
}

export interface ResetIssued {
  reset_token: string | null;
}

export interface Session {
  id: string;
  user_agent: string | null;
  address: string | null;
  last_seen_at: string;
  current: boolean;
}

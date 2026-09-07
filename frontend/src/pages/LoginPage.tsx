import { useState } from 'react';
import { Alert, Button, Card, Flex, Form, Input, Select, Space, Typography } from 'antd';
import { login, setTokens } from '../api/client';
import type { TenantSummary } from '../api/types';
import type { Session } from '../App';

interface Props {
  onSignedIn: (session: Session) => void;
  mode: 'light' | 'dark';
  onToggleMode: () => void;
}

/**
 * Sign in, with the tenant-selection step decision D2 requires.
 *
 * A user can belong to several tenants, so login is two-phase: credentials first, and if more
 * than one membership comes back, a choice. An access token is scoped to exactly one tenant,
 * which is why the tenant cannot be changed after the fact without a new token.
 */
export function LoginPage({ onSignedIn, mode, onToggleMode }: Props) {
  const [email, setEmail] = useState('owner@nihon-motors.test');
  const [password, setPassword] = useState('Dev_Passw0rd!');
  const [tenants, setTenants] = useState<TenantSummary[] | null>(null);
  const [tenantSlug, setTenantSlug] = useState<string | undefined>();
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (): Promise<void> => {
    setBusy(true);
    setError(null);

    try {
      const result = await login(email, password, tenantSlug);

      if (result.requiresTenantSelection) {
        // Not an error - this user belongs to more than one tenant and has to say which.
        setTenants(result.availableTenants);
        setTenantSlug(result.availableTenants[0]?.slug);
        return;
      }

      if (!result.accessToken || !result.activeTenant) {
        setError('The server returned no access token.');
        return;
      }

      setTokens(result.accessToken, result.refreshToken);
      onSignedIn({ tenant: result.activeTenant, permissions: result.permissions, email });
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Sign in failed.');
    } finally {
      setBusy(false);
    }
  };

  return (
    // The sign-in screen is outside the app shell, so it owns its own full-height frame.
    <Flex
      vertical
      align="center"
      justify="center"
      gap={16}
      style={{ minHeight: '100vh', padding: 24 }}
    >
      <Flex align="center" gap={10}>
        <svg width="26" height="26" viewBox="0 0 24 24" style={{ color: '#2563eb' }}>
          <g fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
            <path d="M3 13l2-5a2 2 0 012-1.4h10A2 2 0 0119 8l2 5v5h-3v-2H6v2H3v-5z" />
            <circle cx="7.5" cy="14.5" r="1.2" />
            <circle cx="16.5" cy="14.5" r="1.2" />
          </g>
        </svg>
        <Typography.Title level={3} style={{ margin: 0 }}>Car Dealer</Typography.Title>
      </Flex>

      <Card title="Sign in" style={{ width: '100%', maxWidth: 400 }}>
      <Form layout="vertical" onFinish={submit}>
        <Form.Item label="Email">
          <Input value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="username" />
        </Form.Item>

        <Form.Item label="Password">
          <Input.Password
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="current-password"
          />
        </Form.Item>

        {tenants && (
          <Form.Item label="Tenant" help="This account belongs to more than one tenant.">
            <Select
              value={tenantSlug}
              onChange={setTenantSlug}
              options={tenants.map((t) => ({ value: t.slug, label: t.name }))}
            />
          </Form.Item>
        )}

        {error && <Alert type="error" message={error} style={{ marginBottom: 16 }} showIcon />}

        <Space direction="vertical" style={{ width: '100%' }}>
          <Button type="primary" htmlType="submit" loading={busy} block>
            {tenants ? 'Continue' : 'Sign in'}
          </Button>

          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            Development fixture. Every seeded account uses the same password, and these accounts
            are never created outside Development.
          </Typography.Text>
        </Space>
      </Form>
      </Card>

      <Button type="text" size="small" onClick={onToggleMode}>
        {mode === 'dark' ? 'Light theme' : 'Dark theme'}
      </Button>
    </Flex>
  );
}

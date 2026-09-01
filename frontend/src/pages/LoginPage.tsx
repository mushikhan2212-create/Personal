import { useState } from 'react';
import { Alert, Button, Card, Form, Input, Select, Space, Typography } from 'antd';
import { login, setTokens } from '../api/client';
import type { TenantSummary } from '../api/types';
import type { Session } from '../App';

interface Props {
  onSignedIn: (session: Session) => void;
}

/**
 * Sign in, with the tenant-selection step decision D2 requires.
 *
 * A user can belong to several tenants, so login is two-phase: credentials first, and if more
 * than one membership comes back, a choice. An access token is scoped to exactly one tenant,
 * which is why the tenant cannot be changed after the fact without a new token.
 */
export function LoginPage({ onSignedIn }: Props) {
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
    <Card title="Sign in" style={{ maxWidth: 460, margin: '48px auto' }}>
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
  );
}

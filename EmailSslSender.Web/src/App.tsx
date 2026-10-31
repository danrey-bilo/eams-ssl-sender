import { useCallback, useEffect, useState } from 'react';
import { t } from './i18n/common';
import { api, Cancelled, connect, errorText, forget, refresh } from './lib/api';
import { isMobile } from './lib/device';
import { useHashRoute } from './lib/hooks';
import { setLang, useLang } from './lib/i18n';
import type { Me, Preferences, SecondFactorChallenge } from './lib/types';
import { Enrollment, Login, PasswordChange, SecondFactor } from './pages/Auth';
import { CertificatesPage } from './pages/Certificates';
import { DeliveryPage } from './pages/Delivery';
import { LinkPage } from './pages/LinkPage';
import { ProfilePage } from './pages/Profile';
import { SecurityPage } from './pages/Security';
import { SettingsPage } from './pages/Settings';
import { UsersPage } from './pages/Users';
import { Spinner } from './ui/bits';
import { Brand, Icon, type IconName } from './ui/Icon';
import { OverlayProvider, useOverlay } from './ui/Overlay';
import { StepUpDialog } from './ui/StepUp';

type Screen =
  | { kind: 'loading' }
  | { kind: 'login'; message?: string }
  | { kind: 'second'; challenge: SecondFactorChallenge }
  | { kind: 'enroll' }
  | { kind: 'password' }
  | { kind: 'app'; me: Me };

const NAV: Array<[key: string, icon: IconName]> = [
  ['delivery', 'send'],
  ['certificates', 'cert'],
  ['users', 'users'],
  ['settings', 'settings'],
  ['security', 'lock'],
];

/** Заголовки разделов — на языке интерфейса в момент вызова. */
const title = (key: string): string => ({
  delivery: t('Рассылка'),
  certificates: t('Сертификаты'),
  users: t('Пользователи'),
  settings: t('Настройки'),
  security: t('Безопасность'),
  profile: t('Профиль'),
})[key] ?? key;

const PAGES = new Set([...NAV.map(([key]) => key), 'profile']);

/** Старые адреса разделов настроек. */
const MOVED: Record<string, string> = { mail: '#/settings/mail', keymanager: '#/settings/keymanager' };

export function App() {
  // Смена языка перерисовывает всё дерево.
  useLang();

  // Ссылки из писем — без сессии и каркаса панели.
  if (location.pathname === '/activate') return <OverlayProvider><LinkPage kind="activate" /></OverlayProvider>;
  if (location.pathname === '/password-reset') return <OverlayProvider><LinkPage kind="reset" /></OverlayProvider>;

  return <OverlayProvider><Panel /></OverlayProvider>;
}

function Panel() {
  const [screen, setScreen] = useState<Screen>({ kind: 'loading' });
  const { show } = useOverlay();

  /** Настройка фактора и смена пароля — раньше всего. */
  const enter = useCallback(async () => {
    try {
      const me = await api.get<Me>('/api/me');
      // Язык — свой у каждого; без предпочтений остаётся язык браузера.
      const preferences = await api.get<Preferences>('/api/me/preferences').catch(() => null);
      setLang(preferences?.effective['ui.locale']);
      if (me.must_enroll_two_factor) return setScreen({ kind: 'enroll' });
      if (me.must_change_password) return setScreen({ kind: 'password' });
      if (!location.hash.startsWith('#/')) history.replaceState(null, '', '#/delivery');
      setScreen({ kind: 'app', me });
    } catch (err) {
      if (!(err instanceof Cancelled)) setScreen({ kind: 'login', message: errorText(err) });
    }
  }, []);

  const logout = useCallback(async () => {
    await api.post('/api/auth/logout').catch(() => null);
    forget();
    setScreen({ kind: 'login' });
  }, []);

  useEffect(() => {
    connect({
      signedOut: message => setScreen({ kind: 'login', message }),
      confirmIdentity: minutes => show(close => <StepUpDialog minutes={minutes} onClose={close} />),
    });
    void refresh().then(ok => (ok ? enter() : setScreen({ kind: 'login' })));
  }, [enter, show]);

  switch (screen.kind) {
    case 'loading':
      return <Spinner />;
    case 'login':
      return <Login message={screen.message} onEntered={enter} onSecondFactor={challenge => setScreen({ kind: 'second', challenge })} />;
    case 'second':
      return <SecondFactor challenge={screen.challenge} onEntered={enter} onBack={message => setScreen({ kind: 'login', message })} />;
    case 'enroll':
      return <Enrollment onDone={enter} onLogout={logout} />;
    case 'password':
      return <PasswordChange onDone={enter} onLogout={logout} />;
    case 'app':
      return <Shell me={screen.me} onRenamed={enter} onLogout={logout} />;
  }
}

function initials(me: Me): string {
  const source = (me.display_name || me.email).trim();
  const parts = source.split(/[\s@._-]+/).filter(Boolean);
  return ((parts[0]?.[0] ?? '') + (parts[1]?.[0] ?? '')).toUpperCase() || '?';
}

function Shell({ me, onRenamed, onLogout }: { me: Me; onRenamed: () => void; onLogout: () => void }) {
  const route = useHashRoute();
  const [first = '', second] = route;

  useEffect(() => {
    if (MOVED[first]) history.replaceState(null, '', MOVED[first]);
  }, [first]);

  const page = MOVED[first] ? 'settings' : PAGES.has(first) ? first : 'delivery';
  const sub = MOVED[first] ? first : second;

  const heading = title(page);
  useEffect(() => { document.title = heading + ' · EAMS ssl sender'; }, [heading]);

  const content = (
    <main key={page}>
      {page === 'delivery' && <DeliveryPage />}
      {page === 'certificates' && <CertificatesPage />}
      {page === 'users' && <UsersPage myId={me.id} />}
      {page === 'settings' && <SettingsPage section={sub} myEmail={me.email} />}
      {page === 'security' && <SecurityPage />}
      {page === 'profile' && <ProfilePage onRenamed={onRenamed} onLogout={onLogout} />}
    </main>
  );

  const avatar = (
    <a href="#/profile" className={'avatar' + (page === 'profile' ? ' active' : '')} title={me.email} aria-label={t('Профиль')}>
      {initials(me)}
    </a>
  );

  if (isMobile) {
    return (
      <>
        <header className="top">
          <Brand />
          {avatar}
        </header>
        {content}
        <nav className="tabbar" aria-label={t('Разделы')}>
          {NAV.map(([key, icon]) => (
            <a key={key} href={'#/' + key} className={key === page ? 'active' : ''} aria-current={key === page ? 'page' : undefined}>
              <Icon name={icon} />
              <span>{title(key)}</span>
            </a>
          ))}
        </nav>
      </>
    );
  }

  return (
    <div className="shell">
      <header className="top">
        <Brand />
        <nav className="nav" aria-label={t('Разделы')}>
          {NAV.map(([key, icon]) => (
            <a key={key} href={'#/' + key} className={key === page ? 'active' : ''} aria-current={key === page ? 'page' : undefined}>
              <Icon name={icon} />
              {title(key)}
            </a>
          ))}
        </nav>
        <div className="who">
          <a href="#/profile" className={'who-name' + (page === 'profile' ? ' active' : '')} title={me.email}>
            <span className="avatar small">{initials(me)}</span>
            <span>{me.display_name || me.email}</span>
          </a>
          <button type="button" className="icon" title={t('Выйти')} onClick={onLogout}><Icon name="logout" /></button>
        </div>
      </header>
      {content}
    </div>
  );
}

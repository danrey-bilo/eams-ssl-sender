import { useCallback, useEffect, useState } from 'react';
import { api, Cancelled, connect, errorText, forget, refresh } from './lib/api';
import { useHashRoute } from './lib/hooks';
import type { Me, SecondFactorChallenge } from './lib/types';
import { Enrollment, Login, PasswordChange, SecondFactor } from './pages/Auth';
import { DeliveryPage } from './pages/Delivery';
import { KeyManagerPage } from './pages/KeyManager';
import { LinkPage } from './pages/LinkPage';
import { MailPage } from './pages/Mail';
import { ProfilePage } from './pages/Profile';
import { UsersPage } from './pages/Users';
import { Spinner } from './ui/bits';
import { Brand, Icon } from './ui/Icon';
import { OverlayProvider, useOverlay } from './ui/Overlay';
import { StepUpDialog } from './ui/StepUp';

type Screen =
  | { kind: 'loading' }
  | { kind: 'login'; message?: string }
  | { kind: 'second'; challenge: SecondFactorChallenge }
  | { kind: 'enroll' }
  | { kind: 'password' }
  | { kind: 'app'; me: Me };

const NAV: Array<[string, string]> = [
  ['delivery', 'Рассылка'],
  ['keymanager', 'Key Manager'],
  ['mail', 'Почта'],
  ['users', 'Пользователи'],
];

const TITLES: Record<string, string> = Object.fromEntries([...NAV, ['profile', 'Профиль']]);

export function App() {
  // Страницы ссылок из писем живут без сессии и без каркаса панели.
  if (location.pathname === '/activate') return <OverlayProvider><LinkPage kind="activate" /></OverlayProvider>;
  if (location.pathname === '/password-reset') return <OverlayProvider><LinkPage kind="reset" /></OverlayProvider>;

  return <OverlayProvider><Panel /></OverlayProvider>;
}

function Panel() {
  const [screen, setScreen] = useState<Screen>({ kind: 'loading' });
  const { show } = useOverlay();

  /** Куда пускать вошедшего: настройка фактора и смена пароля — раньше всего остального. */
  const enter = useCallback(async () => {
    try {
      const me = await api.get<Me>('/api/me');
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
      confirmIdentity: () => show(close => <StepUpDialog onClose={close} />),
      signedOut: message => setScreen({ kind: 'login', message }),
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

function Shell({ me, onRenamed, onLogout }: { me: Me; onRenamed: () => void; onLogout: () => void }) {
  const route = useHashRoute();
  const page = route in TITLES ? route : 'delivery';

  useEffect(() => { document.title = TITLES[page] + ' · SSL Sender'; }, [page]);

  return (
    <>
      <header className="top">
        <Brand />
        <nav className="nav">
          {NAV.map(([key, title]) => (
            <a key={key} href={'#/' + key} className={key === page ? 'active' : ''}>{title}</a>
          ))}
        </nav>
        <div className="who">
          <a href="#/profile" className={page === 'profile' ? 'active' : ''} title={me.email}>{me.display_name || me.email}</a>
          <button type="button" className="icon" title="Выйти" onClick={onLogout}><Icon name="logout" /></button>
        </div>
      </header>
      <main key={page}>
        {page === 'delivery' && <DeliveryPage />}
        {page === 'keymanager' && <KeyManagerPage />}
        {page === 'mail' && <MailPage myEmail={me.email} />}
        {page === 'users' && <UsersPage myId={me.id} />}
        {page === 'profile' && <ProfilePage onRenamed={onRenamed} onLogout={onLogout} />}
      </main>
    </>
  );
}

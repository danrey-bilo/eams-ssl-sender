// Режим панели — по режиму браузера, а не по размеру окна.
//
// ⚠ Решение владельца: «телефонная» панель — когда Chrome в режиме смартфона, «компьютерная» —
// когда в режиме ПК (в том числе «Версия для ПК» на телефоне). Ширина экрана и его соотношение
// сторон не решают ничего: узкое окно на компьютере остаётся компьютерной панелью.
//
// Chrome сообщает режим прямо (`navigator.userAgentData.mobile`, он же заголовок Sec-CH-UA-Mobile):
// «Версия для ПК» переключает его в false. Где этого признака нет (Safari, Firefox) — по строке
// агента: на телефоне в ней есть «Mobile»/«iPhone», в режиме ПК — нет.

export type Device = 'mobile' | 'desktop';

interface UserAgentData {
  mobile?: boolean;
}

export function detectDevice(): Device {
  const data = (navigator as Navigator & { userAgentData?: UserAgentData }).userAgentData;
  if (data && typeof data.mobile === 'boolean') return data.mobile ? 'mobile' : 'desktop';
  return /Android.+Mobile|iPhone|iPod|Windows Phone|IEMobile|Opera Mini|Mobi/i.test(navigator.userAgent)
    ? 'mobile'
    : 'desktop';
}

/** Режим на всю жизнь страницы: браузер меняет его только перезагрузкой. */
export const device: Device = detectDevice();

export const isMobile = device === 'mobile';

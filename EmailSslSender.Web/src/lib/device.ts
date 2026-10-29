// Раскладка — по режиму браузера, не по ширине окна (решение владельца): «Версия для ПК» — это ПК.
// Chrome сообщает режим `userAgentData.mobile`; Safari и Firefox — по строке агента.

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

/** Меняется только перезагрузкой. */
export const device: Device = detectDevice();

export const isMobile = device === 'mobile';

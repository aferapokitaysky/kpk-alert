import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { GoogleLoginLink } from './GoogleLoginLink';

describe('посилання входу Google', () => {
  it('веде на поточний endpoint входу, а декоративний знак прихований від читачів екрана', () => {
    render(<GoogleLoginLink className="login-link">Увійти через Google</GoogleLoginLink>);

    expect(screen.getByRole('link', { name: 'Увійти через Google' })).toHaveAttribute(
      'href',
      '/api/v1/auth/login',
    );
    expect(document.querySelector('.google-mark')).toHaveAttribute('aria-hidden', 'true');
  });
});

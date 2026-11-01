// The dev server forwards /api to the .NET API, so the browser sees one origin. The target is configurable so the end-to-end tests can run
// their own stack on other ports without touching a development one.
module.exports = {
  '/api': {
    target: process.env.API_TARGET || 'http://127.0.0.1:5247',
    secure: false,
  },
};

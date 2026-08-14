import PropTypes from 'prop-types';
import React from 'react';
import FieldSet from 'Components/FieldSet';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import { inputTypes } from 'Helpers/Props';

// fork18: UI knob for the command-execution reaper. Literal label/helpText (no translate() key) because the
// overlay does not ship backend localization; the field name maps to HostConfigResource.CommandTimeout.
function TaskSettings(props) {
  const {
    settings,
    onInputChange
  } = props;

  const {
    commandTimeout
  } = settings;

  return (
    <FieldSet legend="Task Handling">
      <FormGroup>
        <FormLabel>Command Timeout</FormLabel>

        <FormInputGroup
          type={inputTypes.NUMBER}
          name="commandTimeout"
          unit="minutes"
          min={0}
          helpText="Abandon a task running longer than this many minutes so it stops holding a worker (0 = disabled). Set generously - only a genuinely hung task should ever hit it."
          onChange={onInputChange}
          {...commandTimeout}
        />
      </FormGroup>
    </FieldSet>
  );
}

TaskSettings.propTypes = {
  settings: PropTypes.object.isRequired,
  onInputChange: PropTypes.func.isRequired
};

export default TaskSettings;
